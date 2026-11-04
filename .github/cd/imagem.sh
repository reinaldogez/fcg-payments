#!/usr/bin/env bash
# publicacao da imagem do commit, um subcomando por step do job imagem do cd.yml:
#   guarda     decide se a imagem precisa ser construida (a tag do ecr ja existe?)
#   construir  build unico com as tres tags (ecr, ghcr:<sha>, ghcr:latest)
#   escanear   trivy sobre o tar da imagem, so reporta
#   publicar   ghcr primeiro, ecr por ultimo
#   verificar  confere que o ecr guardou um manifest de uma plataforma
# os valores passam entre steps por $GITHUB_ENV (variaveis) e $GITHUB_OUTPUT (o que
# os ifs do workflow leem).
set -euo pipefail

# o cluster so aceita manifest simples; indice oci ou manifest list quebram o pull
readonly MEDIA_TYPES_ACEITOS=(
  application/vnd.docker.distribution.manifest.v2+json
  application/vnd.oci.image.manifest.v1+json
)

falha() {
  echo "::error::$*"
  echo "erro: $*" >&2
  exit 1
}

aviso() { echo "::warning::$*"; }
nota() { echo "::notice::$*"; }

exige() {
  local nome
  for nome in "$@"; do
    [[ -n "${!nome:-}" ]] || falha "variavel $nome ausente"
  done
}

resumo() {
  exige GITHUB_STEP_SUMMARY
  printf '%s\n' "$@" >> "$GITHUB_STEP_SUMMARY"
}

# exporta para os steps seguintes e para o resto deste processo
exporta() {
  exige GITHUB_ENV
  printf '%s=%s\n' "$1" "$2" >> "$GITHUB_ENV"
  export "$1=$2"
}

decide() {
  exige GITHUB_OUTPUT
  printf '%s=%s\n' "$1" "$2" >> "$GITHUB_OUTPUT"
}

# erros da cli da aws comecam com uma linha vazia; a primeira com texto e a util
primeira_linha() {
  printf '%s\n' "$1" | sed '/^[[:space:]]*$/d' | head -n 1
}

versao() {
  local v
  v="$("$@" 2>&1 | head -n 1)" || true
  printf '%s' "${v:-indisponivel}"
}

versoes() {
  echo "versoes: aws=[$(versao aws --version)] docker=[$(versao docker --version)] jq=[$(versao jq --version)] git=[$(versao git --version)]"
}

# nomes derivados do repositorio; tudo que vem do ambiente e conferido antes de
# qualquer chamada a nuvem, para que uma variavel copiada para o repositorio
# errado pare aqui e nao publique imagem no lugar de outro servico
derivar() {
  exige GITHUB_REPOSITORY GITHUB_SHA AWS_REGION
  [[ "$GITHUB_REPOSITORY" =~ ^reinaldogez/fcg-(identity|catalog|payments)$ ]] \
    || falha "repositorio '$GITHUB_REPOSITORY' nao e um servico com entrega continua"
  local nome_servico="${BASH_REMATCH[1]}"
  [[ "$GITHUB_SHA" =~ ^[0-9a-f]{40}$ ]] || falha "GITHUB_SHA nao e um sha completo: '$GITHUB_SHA'"
  [[ "${AWS_ROLE_ARN:-}" == *":role/fcg-cd-$nome_servico" ]] \
    || falha "AWS_ROLE_ARN nao termina em :role/fcg-cd-$nome_servico (variavel do repositorio errado?)"

  local conta
  conta="$(aws sts get-caller-identity --query Account --output text)" \
    || falha "aws sts get-caller-identity falhou"
  conta="${conta//[[:space:]]/}"
  [[ "$conta" =~ ^[0-9]{12}$ ]] || falha "conta devolvida pelo sts fora do formato esperado"

  local endereco="$conta.dkr.ecr.$AWS_REGION.amazonaws.com"
  exporta SERVICO "$nome_servico"
  exporta REGISTRY "$endereco"
  exporta ECR_REPO "fcg-$nome_servico"
  exporta IMAGEM_ECR "$endereco/fcg-$nome_servico:$GITHUB_SHA"
}

cmd_guarda() {
  derivar
  exporta IMAGEM_GHCR "ghcr.io/${GITHUB_REPOSITORY,,}"

  # so a ausencia da tag libera o build; qualquer outro erro (repositorio
  # inexistente, permissao, rede) falha em vez de reconstruir as cegas
  local erro_aws
  if erro_aws="$(aws ecr describe-images --repository-name "$ECR_REPO" \
      --image-ids "imageTag=$GITHUB_SHA" 2>&1 >/dev/null)"; then
    decide construir false
    exporta CONSTRUIR false
    nota "imagem $ECR_REPO:$GITHUB_SHA ja esta no ECR — build e push pulados"
  elif [[ "$erro_aws" == *ImageNotFoundException* ]]; then
    decide construir true
    exporta CONSTRUIR true
    echo "imagem $ECR_REPO:$GITHUB_SHA ausente no ECR: segue para o build"
  else
    falha "consulta da tag no ECR falhou: $(primeira_linha "$erro_aws")"
  fi
}

cmd_construir() {
  exige IMAGEM_ECR IMAGEM_GHCR GITHUB_SHA GH_TOKEN
  local inicio=$SECONDS
  # builder padrao do daemon: sem atestacoes a imagem sai com manifest de uma
  # plataforma; o token do restore entra como secret do buildkit, nunca como arg
  docker buildx build --load --provenance=false --sbom=false \
    --secret id=gh_token,env=GH_TOKEN \
    -t "$IMAGEM_ECR" \
    -t "$IMAGEM_GHCR:$GITHUB_SHA" \
    -t "$IMAGEM_GHCR:latest" \
    .
  resumo "### Build" "" "- imagem: \`$IMAGEM_ECR\`" "- duracao: $((SECONDS - inicio)) s"
}

scan_nao_executado() {
  resumo "### Scan de vulnerabilidades" "" "scan nao executado: $1"
  falha "scan nao executado: $1"
}

cmd_escanear() {
  exige IMAGEM_ECR TRIVY_IMAGE RUNNER_TEMP
  local inicio=$SECONDS
  local tar="$RUNNER_TEMP/imagem.tar" json="$RUNNER_TEMP/trivy.json"

  docker save "$IMAGEM_ECR" -o "$tar" || scan_nao_executado "docker save da imagem falhou"

  # ferramenta de terceiros: so le o tar, sem socket do docker, sem variaveis
  # do job e sem rede do host
  local rc=0
  docker run --rm -v "$tar:/img.tar:ro" "$TRIVY_IMAGE" \
    image --input /img.tar --severity CRITICAL,HIGH --exit-code 0 \
    --timeout 5m --format json --quiet > "$json" || rc=$?
  ((rc == 0)) || scan_nao_executado "trivy saiu com codigo $rc"
  jq empty "$json" 2>/dev/null || scan_nao_executado "saida do trivy nao e json valido"

  local criticas altas segredos
  criticas="$(jq '[.Results[]?.Vulnerabilities[]? | select(.Severity == "CRITICAL")] | length' "$json")"
  altas="$(jq '[.Results[]?.Vulnerabilities[]? | select(.Severity == "HIGH")] | length' "$json")"
  segredos="$(jq '[.Results[]?.Secrets[]?] | length' "$json")"

  resumo "### Scan de vulnerabilidades" "" \
    "- CRITICAL: $criticas" "- HIGH: $altas" "- segredos: $segredos" \
    "- duracao: $((SECONDS - inicio)) s" ""
  if ((criticas + altas > 0)); then
    # do conteudo de segredos so a contagem sai daqui; a tabela e so de pacotes
    resumo "| Severidade | Pacote | CVE | Instalada | Corrigida |" "|---|---|---|---|---|"
    jq -r '[.Results[]?.Vulnerabilities[]?]
      | sort_by(if .Severity == "CRITICAL" then 0 else 1 end)
      | .[:50][]
      | "| \(.Severity) | \(.PkgName) | \(.VulnerabilityID) | \(.InstalledVersion) | \(.FixedVersion // "-") |"' \
      "$json" >> "$GITHUB_STEP_SUMMARY"
  fi
  if ((criticas + altas + segredos > 0)); then
    aviso "trivy: $criticas CRITICAL, $altas HIGH, $segredos segredo(s) na imagem"
  fi
}

head_da_main() {
  local saida
  saida="$(git ls-remote "https://github.com/$GITHUB_REPOSITORY" refs/heads/main)" || return 1
  saida="${saida%%[[:space:]]*}"
  [[ "$saida" =~ ^[0-9a-f]{40}$ ]] || return 1
  printf '%s' "$saida"
}

cmd_publicar() {
  exige GH_TOKEN GITHUB_ACTOR GITHUB_REPOSITORY GITHUB_SHA IMAGEM_GHCR IMAGEM_ECR REGISTRY ECR_REPO
  # no ecr so entra a tag do sha completo; latest e exclusivo do ghcr
  [[ "$IMAGEM_ECR" == "$REGISTRY/$ECR_REPO:$GITHUB_SHA" && "$GITHUB_SHA" =~ ^[0-9a-f]{40}$ ]] \
    || falha "IMAGEM_ECR fora do formato <registry>/<repo>:<sha>"

  printf '%s' "$GH_TOKEN" | docker login ghcr.io -u "$GITHUB_ACTOR" --password-stdin
  docker push "$IMAGEM_GHCR:$GITHUB_SHA"

  # latest so acompanha a main: um run atrasado nao pode puxar latest para tras
  local head
  head="$(head_da_main)" || falha "consulta do HEAD da main falhou"
  if [[ "$head" == "$GITHUB_SHA" ]]; then
    docker push "$IMAGEM_GHCR:latest"
  else
    aviso "latest nao atualizado: $GITHUB_SHA nao e mais o HEAD da main ($head)"
  fi

  # o ecr fica por ultimo: a tag la e o marco de publicacao completa, e a guarda
  # de tag de um re-run confia nela
  aws ecr get-login-password | docker login "$REGISTRY" -u AWS --password-stdin
  docker push "$IMAGEM_ECR"
}

cmd_verificar() {
  exige ECR_REPO GITHUB_SHA IMAGEM_ECR
  local tipo
  tipo="$(aws ecr describe-images --repository-name "$ECR_REPO" \
    --image-ids "imageTag=$GITHUB_SHA" \
    --query 'imageDetails[0].imageManifestMediaType' --output text)" \
    || falha "consulta do manifest no ECR falhou"
  tipo="${tipo//[[:space:]]/}"

  local aceito
  for aceito in "${MEDIA_TYPES_ACEITOS[@]}"; do
    if [[ "$tipo" == "$aceito" ]]; then
      local nota_tag=""
      [[ "${CONSTRUIR:-}" == false ]] && nota_tag=" (tag preexistente)"
      resumo "### Manifest no ECR" "" "- \`$IMAGEM_ECR\`: \`$tipo\`$nota_tag"
      return 0
    fi
  done
  falha "manifest de $ECR_REPO:$GITHUB_SHA no ECR e '$tipo', nao uma imagem de uma plataforma; a correcao e humana: apagar a imagem no ECR com a credencial de operacao e dar re-run"
}

uso() {
  echo "::error::subcomando invalido: '${1:-}'"
  echo "uso: imagem.sh guarda|construir|escanear|publicar|verificar" >&2
  exit 2
}

main() {
  local sub="${1:-}"
  case "$sub" in
    guarda | construir | escanear | publicar | verificar) ;;
    *) uso "$sub" ;;
  esac
  versoes
  case "$sub" in
    guarda) cmd_guarda ;;
    construir) cmd_construir ;;
    escanear) cmd_escanear ;;
    publicar) cmd_publicar ;;
    verificar) cmd_verificar ;;
  esac
}

main "$@"
