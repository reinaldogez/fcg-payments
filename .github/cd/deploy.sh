#!/usr/bin/env bash
# atualizacao do cluster com a imagem do commit, um subcomando por step do job
# deploy do cd.yml:
#   kubectl    instala o kubectl fixado, conferido por sha256
#   guardas    decide se ha cluster e stack para implantar (senao adia com run verde)
#   migration  recria o job de migration com a imagem nova e espera terminar
#   rollout    troca a imagem do deployment; se nao convergir, desfaz
#   evidencia  registra o que a identidade do pipeline pode ler no namespace
# os valores passam entre steps por $GITHUB_ENV (variaveis) e $GITHUB_OUTPUT (o que
# os ifs do workflow leem).
set -euo pipefail

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
  echo "versoes: aws=[$(versao aws --version)] jq=[$(versao jq --version)] git=[$(versao git --version)]"
}

# arquivo de trabalho para o stderr das consultas; some ao fim do processo
ERRO_TMP="$(mktemp)"
readonly ERRO_TMP
trap 'rm -f "$ERRO_TMP"' EXIT

# nomes derivados do repositorio; tudo que vem do ambiente e conferido antes de
# qualquer chamada a nuvem, para que uma variavel copiada para o repositorio
# errado pare aqui e nao implante no lugar de outro servico
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

head_da_main() {
  local saida
  saida="$(git ls-remote "https://github.com/$GITHUB_REPOSITORY" refs/heads/main)" || return 1
  saida="${saida%%[[:space:]]*}"
  [[ "$saida" =~ ^[0-9a-f]{40}$ ]] || return 1
  printf '%s' "$saida"
}

# adiar e sair verde: a imagem ja esta no ecr e a proxima abertura de sessao
# converge o cluster para a main
adia() {
  decide implantar false
  exit 0
}

cmd_kubectl() {
  exige KUBECTL_VERSION KUBECTL_SHA256 RUNNER_TEMP GITHUB_PATH
  [[ "$KUBECTL_VERSION" =~ ^v[0-9]+\.[0-9]+\.[0-9]+$ ]] || falha "KUBECTL_VERSION invalida: '$KUBECTL_VERSION'"
  [[ "$KUBECTL_SHA256" =~ ^[0-9a-f]{64}$ ]] || falha "KUBECTL_SHA256 nao e um sha256"

  local dir="$RUNNER_TEMP/bin"
  mkdir -p "$dir"
  curl -fsSL --retry 3 -o "$dir/kubectl" \
    "https://dl.k8s.io/release/$KUBECTL_VERSION/bin/linux/amd64/kubectl" \
    || falha "download do kubectl $KUBECTL_VERSION falhou"

  local calculado
  calculado="$(sha256sum "$dir/kubectl")"
  calculado="${calculado%% *}"
  if [[ "$calculado" != "$KUBECTL_SHA256" ]]; then
    rm -f "$dir/kubectl"
    falha "sha256 do kubectl baixado ($calculado) difere do fixado"
  fi
  chmod +x "$dir/kubectl"
  echo "$dir" >> "$GITHUB_PATH"
  "$dir/kubectl" version --client
}

# classifica a resposta de um get no namespace; o texto vem do kubectl e cobre
# o 401 (identidade sem access entry) separado do 403 (rbac sem o verbo)
CLASSE=""
ERRO_K8S=""
consulta_k8s() {
  local rc=0
  kubectl get "$1" -n "$K8S_NAMESPACE" --request-timeout=20s >/dev/null 2>"$ERRO_TMP" || rc=$?
  ERRO_K8S="$(<"$ERRO_TMP")"
  if ((rc == 0)); then
    CLASSE=ok
  elif [[ "$ERRO_K8S" == *"(NotFound)"* ]]; then
    CLASSE=ausente
  elif [[ "$ERRO_K8S" == *"(Unauthorized)"* || "$ERRO_K8S" == *"must be logged in"* ]]; then
    CLASSE=sem_acesso
  elif [[ "$ERRO_K8S" == *"(Forbidden)"* ]]; then
    CLASSE=proibido
  else
    CLASSE=outro
  fi
}

# 401 adia (sessao abrindo ou fechando); 403 e qualquer outro erro falham
trata_acesso() {
  case "$CLASSE" in
    sem_acesso)
      aviso "sem acesso ao cluster (sessao abrindo ou fechando, ou access entry ausente) — deploy adiado"
      adia
      ;;
    proibido) falha "RBAC do pipeline sem permissao em $1: $(primeira_linha "$ERRO_K8S")" ;;
    *) falha "consulta de $1 falhou: $(primeira_linha "$ERRO_K8S")" ;;
  esac
}

cmd_guardas() {
  derivar
  exige EKS_CLUSTER K8S_NAMESPACE RUNNER_TEMP

  # um deploy atrasado nunca sobrescreve um commit mais novo da main
  local head
  head="$(head_da_main)" || falha "consulta do HEAD da main falhou"
  if [[ "$head" != "$GITHUB_SHA" ]]; then
    nota "deploy superado: $GITHUB_SHA nao e mais o HEAD da main ($head)"
    adia
  fi

  # o cluster so existe durante a sessao; sem ele a entrega termina no ecr
  local status rc=0 erro_aws
  status="$(aws eks describe-cluster --name "$EKS_CLUSTER" \
    --query cluster.status --output text 2>"$ERRO_TMP")" || rc=$?
  erro_aws="$(<"$ERRO_TMP")"
  if ((rc != 0)); then
    if [[ "$erro_aws" == *ResourceNotFoundException* ]]; then
      nota "cluster desligado — imagem publicada, deploy adiado"
      adia
    fi
    falha "consulta do cluster $EKS_CLUSTER falhou: $(primeira_linha "$erro_aws")"
  fi
  status="${status//[[:space:]]/}"
  case "$status" in
    ACTIVE | UPDATING) ;;
    CREATING | DELETING)
      nota "cluster $status (sessao abrindo ou fechando) — deploy adiado"
      adia
      ;;
    *) falha "cluster $EKS_CLUSTER em estado inesperado: '$status'" ;;
  esac

  local kubeconfig="$RUNNER_TEMP/kubeconfig"
  aws eks update-kubeconfig --name "$EKS_CLUSTER" --kubeconfig "$kubeconfig" \
    || falha "update-kubeconfig do cluster $EKS_CLUSTER falhou"
  export KUBECONFIG="$kubeconfig"

  local alvo_api="$SERVICO-api" alvo_job="$SERVICO-migrate"
  consulta_k8s "deployment/$alvo_api"
  case "$CLASSE" in
    ok) ;;
    ausente)
      nota "stack ainda nao esta de pe no cluster — deploy adiado"
      adia
      ;;
    *) trata_acesso "deployment/$alvo_api" ;;
  esac

  # com o deployment de pe o job precisa existir: a recriacao parte dele
  consulta_k8s "job/$alvo_job"
  case "$CLASSE" in
    ok) ;;
    ausente) falha "Job de migration ausente com o Deployment de pe ($alvo_job)" ;;
    *) trata_acesso "job/$alvo_job" ;;
  esac

  exporta KUBECONFIG "$kubeconfig"
  exporta DEPLOYMENT "$alvo_api"
  exporta CONTAINER "$alvo_api"
  exporta JOB "$alvo_job"
  exporta JOB_CONTAINER migrate
  decide implantar true
}

# spec do job e imutavel: a copia do job vivo perde o que o servidor gera
# (identificadores, seletor, rotulos de controle, status) e so troca a imagem
# do container de migration; o resto (sa, init containers, envFrom) fica igual
FILTRO_JOB="$(cat << 'JQ'
def sem_gerados: del(.["controller-uid"], .["batch.kubernetes.io/controller-uid"],
                     .["job-name"], .["batch.kubernetes.io/job-name"]);
del(.metadata.uid, .metadata.resourceVersion, .metadata.creationTimestamp,
    .metadata.managedFields, .metadata.generation, .metadata.selfLink,
    .status, .spec.selector)
| .metadata.annotations = ((.metadata.annotations // {})
    | del(.["kubectl.kubernetes.io/last-applied-configuration"], .["batch.kubernetes.io/job-tracking"]))
| if (.metadata.annotations | length) == 0 then del(.metadata.annotations) else . end
| .metadata.labels = ((.metadata.labels // {}) | sem_gerados)
| if (.metadata.labels | length) == 0 then del(.metadata.labels) else . end
| .spec.template.metadata.labels = ((.spec.template.metadata.labels // {}) | sem_gerados)
| if (.spec.template.metadata.labels | length) == 0 then del(.spec.template.metadata.labels) else . end
| (.spec.template.spec.containers[] | select(.name == $container) | .image) = $imagem
JQ
)"
readonly FILTRO_JOB

# cada linha impressa e uma assercao violada; vazio = json pronto para o create
ASSERCOES_JOB="$(cat << 'JQ'
def gerados: ["controller-uid", "batch.kubernetes.io/controller-uid", "job-name", "batch.kubernetes.io/job-name"];
def tem_gerado: (. // {}) | keys | any(IN(gerados[]));
[ (if ([.spec.template.spec.containers[]? | select(.name == $container)] | length) == 1
     then empty else "nao ha exatamente um container \($container)" end),
  (if [.spec.template.spec.containers[]? | select(.name == $container) | .image] == [$imagem]
     then empty else "imagem do container \($container) diferente de \($imagem)" end),
  (if [.spec.template.spec.initContainers[]?.image] == [$orig[0].spec.template.spec.initContainers[]?.image]
     then empty else "imagens dos initContainers diferentes do job vivo" end),
  (if .spec.template.spec.serviceAccountName == $orig[0].spec.template.spec.serviceAccountName
     then empty else "serviceAccountName diferente do job vivo" end),
  (if (.metadata.labels | tem_gerado) then "rotulo gerado em metadata.labels" else empty end),
  (if (.spec.template.metadata.labels | tem_gerado) then "rotulo gerado em spec.template.metadata.labels" else empty end),
  (if .spec.selector == null then empty else "spec.selector presente" end),
  (if .status == null then empty else "status presente" end),
  (if .metadata.uid == null then empty else "metadata.uid presente" end),
  (if .metadata.resourceVersion == null then empty else "metadata.resourceVersion presente" end)
] | .[]
JQ
)"
readonly ASSERCOES_JOB

# transforma o job vivo para a imagem pedida e confere o resultado
prepara_job() {
  local original="$1" imagem="$2" destino="$3"
  jq --arg imagem "$imagem" --arg container "$JOB_CONTAINER" "$FILTRO_JOB" "$original" > "$destino" \
    || falha "transformacao do job $JOB falhou"
  local violacoes
  violacoes="$(jq -r --arg imagem "$imagem" --arg container "$JOB_CONTAINER" \
    --slurpfile orig "$original" "$ASSERCOES_JOB" "$destino")" \
    || falha "assercoes do job $JOB nao puderam ser avaliadas"
  [[ -z "$violacoes" ]] || falha "job $JOB transformado e invalido, cluster intocado: ${violacoes//$'\n'/; }"
}

cmd_migration() {
  exige JOB JOB_CONTAINER IMAGEM_ECR K8S_NAMESPACE RUNNER_TEMP KUBECONFIG
  local intervalo="${CD_INTERVALO_JOB_S:-5}" teto="${CD_TIMEOUT_JOB_S:-600}"
  [[ "$intervalo" =~ ^[0-9]+$ && "$teto" =~ ^[0-9]+$ ]] || falha "CD_INTERVALO_JOB_S e CD_TIMEOUT_JOB_S devem ser inteiros"

  local original="$RUNNER_TEMP/job-original.json"
  local novo="$RUNNER_TEMP/job-novo.json"
  local restauracao="$RUNNER_TEMP/job-restauracao.json"

  kubectl get "job/$JOB" -n "$K8S_NAMESPACE" -o json > "$original" \
    || falha "leitura do job $JOB falhou"

  # o json de restauracao e preparado antes de mexer no cluster: se o create do
  # novo falhar, o job volta com a imagem que tinha
  local imagem_original
  imagem_original="$(jq -r --arg container "$JOB_CONTAINER" \
    '[.spec.template.spec.containers[]? | select(.name == $container) | .image] | first // empty' "$original")"
  [[ -n "$imagem_original" ]] || falha "job $JOB sem container $JOB_CONTAINER"
  prepara_job "$original" "$IMAGEM_ECR" "$novo"
  prepara_job "$original" "$imagem_original" "$restauracao"

  # o servidor valida o json com outro nome antes de qualquer delete
  jq --arg nome "$JOB-validacao" '.metadata.name = $nome' "$novo" \
    | kubectl create --dry-run=server -n "$K8S_NAMESPACE" -f - \
    || falha "servidor recusou o job novo na validacao; cluster intocado"

  kubectl delete "job/$JOB" -n "$K8S_NAMESPACE" --cascade=foreground --wait=true --timeout=120s \
    || falha "delete do job $JOB falhou"
  if ! kubectl create -n "$K8S_NAMESPACE" -f "$novo"; then
    echo "::error::create do job $JOB com a imagem nova falhou; recriando o job anterior"
    kubectl create -n "$K8S_NAMESPACE" -f "$restauracao" \
      || falha "create do job novo falhou e a restauracao tambem: job $JOB ausente no cluster"
    falha "create do job $JOB com a imagem nova falhou; job anterior restaurado"
  fi

  local inicio=$SECONDS condicoes motivo
  while :; do
    condicoes="$(kubectl get "job/$JOB" -n "$K8S_NAMESPACE" --request-timeout=20s \
      -o jsonpath='{range .status.conditions[*]}{.type}={.status}{"\n"}{end}')" || condicoes=""
    if grep -qx 'Complete=True' <<< "$condicoes"; then
      local fim
      fim="$(kubectl get "job/$JOB" -n "$K8S_NAMESPACE" -o jsonpath='{.status.completionTime}')" || fim="indisponivel"
      resumo "### Migration" "" "- job \`$JOB\` com \`$IMAGEM_ECR\`" "- completionTime: $fim"
      return 0
    fi
    if grep -qx 'Failed=True' <<< "$condicoes"; then
      motivo="job $JOB falhou"
      break
    fi
    if ((SECONDS - inicio >= teto)); then
      motivo="job $JOB nao terminou em $teto s"
      break
    fi
    sleep "$intervalo"
  done
  kubectl logs "job/$JOB" -n "$K8S_NAMESPACE" --all-containers --tail=200 || true
  falha "$motivo; rollout nao executado"
}

# replicaset do deployment cujo template tem a imagem nova
criacao_rs_novo() {
  local uid
  uid="$(kubectl get "deployment/$DEPLOYMENT" -n "$K8S_NAMESPACE" -o jsonpath='{.metadata.uid}')" || return 1
  kubectl get rs -n "$K8S_NAMESPACE" -l "app=$DEPLOYMENT" -o json \
    | jq -r --arg uid "$uid" --arg imagem "$IMAGEM_ECR" \
      '[.items[]
        | select(any(.metadata.ownerReferences[]?; .uid == $uid))
        | select(any(.spec.template.spec.containers[]?; .image == $imagem))
        | .metadata.creationTimestamp] | sort | last // empty'
}

# diagnostico so com pods e logs: nada de describe nem eventos
diagnostico() {
  kubectl get pods -l "app=$DEPLOYMENT" -n "$K8S_NAMESPACE" -o wide || true
  kubectl get pods -l "app=$DEPLOYMENT" -n "$K8S_NAMESPACE" \
    -o jsonpath='{range .items[*]}{.metadata.name}{":"}{range .status.containerStatuses[*]}{" "}{.name}{"="}{.state.waiting.reason}{end}{"\n"}{end}' || true
  local pod
  pod="$(kubectl get pods -l "app=$DEPLOYMENT" -n "$K8S_NAMESPACE" \
    --sort-by=.metadata.creationTimestamp -o jsonpath='{.items[-1:].metadata.name}')" || pod=""
  if [[ -n "$pod" ]]; then
    kubectl logs "$pod" -c "$CONTAINER" -n "$K8S_NAMESPACE" --tail=100 || true
  fi
}

cmd_rollout() {
  exige DEPLOYMENT CONTAINER JOB IMAGEM_ECR K8S_NAMESPACE KUBECONFIG
  local timeout="${CD_TIMEOUT_ROLLOUT:-300s}"

  kubectl set image "deployment/$DEPLOYMENT" "$CONTAINER=$IMAGEM_ECR" -n "$K8S_NAMESPACE" \
    || falha "set image do deployment $DEPLOYMENT falhou"

  if kubectl rollout status "deployment/$DEPLOYMENT" -n "$K8S_NAMESPACE" --timeout="$timeout"; then
    local fim job_fim rs_criacao
    fim="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    job_fim="$(kubectl get "job/$JOB" -n "$K8S_NAMESPACE" -o jsonpath='{.status.completionTime}')" || job_fim=""
    rs_criacao="$(criacao_rs_novo)" || rs_criacao=""
    resumo "### Rollout" "" \
      "- deployment \`$DEPLOYMENT\` com \`$IMAGEM_ECR\`" \
      "- completionTime do job: ${job_fim:-indisponivel}" \
      "- creationTimestamp do replicaset novo: ${rs_criacao:-indisponivel}" \
      "- fim do rollout: $fim"
    return 0
  fi

  # producao volta para a revisao anterior; a correcao vem por novo commit
  diagnostico
  kubectl rollout undo "deployment/$DEPLOYMENT" -n "$K8S_NAMESPACE" \
    || echo "::error::rollout undo do deployment $DEPLOYMENT falhou"
  kubectl rollout status "deployment/$DEPLOYMENT" -n "$K8S_NAMESPACE" --timeout="$timeout" \
    || echo "::error::deployment $DEPLOYMENT nao convergiu depois do undo"
  falha "rollout falhou; deployment devolvido a revisao anterior"
}

# informativo: nunca falha, o erro do comando vira texto no resumo
cmd_evidencia() {
  local ns="${K8S_NAMESPACE:-fcg}" pergunta resposta
  local -a partes
  local linhas=("### Evidencia de acesso do pipeline" "")
  for pergunta in "get secrets" "list configmaps" "create jobs"; do
    read -r -a partes <<< "$pergunta"
    # can-i sai 1 quando a resposta e no; a saida e o que importa
    resposta="$(kubectl auth can-i "${partes[@]}" -n "$ns" 2>&1)" || true
    resposta="${resposta//$'\n'/ }"
    linhas+=("- \`kubectl auth can-i $pergunta -n $ns\`: ${resposta:-sem resposta}")
  done
  if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
    printf '%s\n' "${linhas[@]}" >> "$GITHUB_STEP_SUMMARY"
  else
    printf '%s\n' "${linhas[@]}"
  fi
}

uso() {
  echo "::error::subcomando invalido: '${1:-}'"
  echo "uso: deploy.sh kubectl|guardas|migration|rollout|evidencia" >&2
  exit 2
}

main() {
  local sub="${1:-}"
  case "$sub" in
    kubectl | guardas | migration | rollout | evidencia) ;;
    *) uso "$sub" ;;
  esac
  versoes
  case "$sub" in
    kubectl) cmd_kubectl ;;
    guardas) cmd_guardas ;;
    migration) cmd_migration ;;
    rollout) cmd_rollout ;;
    evidencia) cmd_evidencia || true ;;
  esac
}

main "$@"
