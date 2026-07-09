# fcg-payments

Microsserviço de **processamento de pagamentos** da plataforma **FIAP Cloud Games (FCG)**. É o
**elo do meio** da saga de compra, em **coreografia** (sem orquestrador): consome o fato publicado
pelo catálogo (`OrderPlacedEvent`), decide via **gateway simulado** e publica o veredito
(`PaymentProcessedEvent`). Não inicia nem finaliza a saga — só o passo do meio.

Não expõe **endpoints REST de negócio**: o trabalho entra pela mensageria; a superfície HTTP são
apenas os health endpoints. Também **não** faz autenticação (não há superfície de negócio a
proteger).

## Sumário

- [fcg-payments](#fcg-payments)
  - [Sumário](#sumário)
  - [O que o serviço faz](#o-que-o-serviço-faz)
    - [Eventos consumidos e publicados](#eventos-consumidos-e-publicados)
    - [Regra do gateway simulado](#regra-do-gateway-simulado)
    - [Superfície HTTP](#superfície-http)
  - [Arquitetura](#arquitetura)
  - [Pré-requisitos](#pré-requisitos)
  - [Token para restaurar o `Fcg.Contracts`](#token-para-restaurar-o-fcgcontracts)
  - [Build e testes locais](#build-e-testes-locais)
  - [Docker](#docker)
    - [Rodando o container](#rodando-o-container)
    - [Variáveis de ambiente](#variáveis-de-ambiente)
  - [Migração (Job de bootstrap)](#migração-job-de-bootstrap)
  - [Observabilidade](#observabilidade)
  - [Health checks](#health-checks)
  - [Imagem no GHCR](#imagem-no-ghcr)
  - [Deploy](#deploy)

## O que o serviço faz

É um **consumer worker**: o fluxo nasce de uma mensagem, não de uma requisição HTTP. A decisão de
pagamento de um pedido vive no agregado `Pagamento` e percorre a saga assim:

1. O catalog publica `OrderPlacedEvent` ao criar um pedido.
2. Este serviço **consome** o evento (Inbox idempotente), executa o gateway simulado e decide
   aprovar/rejeitar — nasce um `Pagamento` já em estado terminal.
3. Na **mesma transação** do consumo, **publica** `PaymentProcessedEvent` (via Outbox) com o
   veredito. O catalog e o notifications reagem ao veredito.

Os dados de usuário/jogo (`UserEmail`/`UserName`/`GameName`) que o evento de entrada carrega são
**trânsito puro**: copiados do evento de entrada para o de saída no escopo do consumo, nunca
persistidos.

### Eventos consumidos e publicados

| Direção | Evento | Exchange / Fila |
| :--- | :--- | :--- |
| **Consome** | `OrderPlacedEvent` | fila `order-placed.fcg-payments` ← exchange `order-placed` |
| **Publica** | `PaymentProcessedEvent` | exchange `payment-processed` (fanout) |

A **idempotência** tem duas camadas, garantindo exatamente **um** `PaymentProcessedEvent` por
pedido:

- **Inbox ativo** do MassTransit (por `MessageId`, em transação única com as escritas de domínio):
  a reentrega da mesma mensagem não duplica o `Pagamento` nem re-publica.
- **Índice único pleno** sobre `OrderId` + consulta prévia no use case: uma duplicata de negócio
  (mesmo pedido, `MessageId` distintos) cai num ramo no-op, sem publicar de novo.

Os contratos vêm do pacote **`Fcg.Contracts`** (não há tipos duplicados localmente).

### Regra do gateway simulado

O gateway é **determinístico e em-processo** (sem I/O): o valor do jogo é comparado a um
threshold.

- `price > threshold` (estrito) → **rejeita** com o motivo `"Valor acima do limite autorizado"`.
- caso contrário → **aprova**. Exatamente `5000` **aprova** (fronteira inclusiva);
  `5000.01` rejeita.
- O threshold tem default **`5000m`** embutido; `Payment__RejectionThreshold` sobrepõe.

### Superfície HTTP

**Não há endpoints REST de negócio** — sem controllers, sem auth, sem OpenAPI. A superfície HTTP
inteira são os três health endpoints (ver [Health checks](#health-checks)).

## Arquitetura

Quatro camadas, com dependência sempre **para dentro**:

```
Api → Infrastructure → Application → Domain
```

- **`Fcg.Payments.Domain`** — o agregado `Pagamento`, value objects (`Preco`,
  `ResultadoAutorizacao`), o enum `StatusPagamento` e as portas (`IPagamentoRepository`,
  `IGatewayPagamento`). Não referencia ninguém.
- **`Fcg.Payments.Application`** — o único use case (`ProcessarPagamentoUseCase`) e options.
  Agnóstica de broker (recebe `IPublishEndpoint`, nunca conhece o RabbitMQ).
- **`Fcg.Payments.Infrastructure`** — EF Core (PostgreSQL), repositório, migrations, mensageria
  (MassTransit: Outbox + consumer + Inbox) e o gateway simulado.
- **`Fcg.Payments.Api`** — host mínimo: health, observabilidade e composição final. É o mesmo
  binário que serve a API-health e o Job de bootstrap.

## Pré-requisitos

- **.NET 10 SDK**
- **PostgreSQL** acessível (dono dos dados de pagamento)
- **RabbitMQ** acessível (transporte dos eventos)
- Acesso de leitura ao feed **GitHub Packages** para restaurar o pacote `Fcg.Contracts`
  (ver abaixo — exige token mesmo sendo público).

## Token para restaurar o `Fcg.Contracts`

O serviço referencia o pacote **`Fcg.Contracts`** (contratos de eventos), publicado no feed
**GitHub Packages**. Esse feed **exige autenticação mesmo para pacotes públicos** — diferente do
`ghcr.io` de imagens, que serve anônimo. Logo, o `dotnet restore` local precisa de um
**Personal Access Token (PAT)** com o escopo **`read:packages`**.

O `nuget.config` versionado declara o source `github-fcg` **sem** credenciais. Forneça o token
**fora do repositório**, de uma destas formas (deixe o `nuget.config` versionado intacto):

**Opção A — `nuget.config` no nível de usuário (recomendado):** grava a credencial no
config global do NuGet (`%AppData%\NuGet\NuGet.Config` no Windows / `~/.nuget/NuGet/NuGet.Config`),
fora do repo:

```bash
dotnet nuget update source github-fcg \
  --username <seu-usuario-github> \
  --password <SEU_PAT_read:packages> \
  --store-password-in-clear-text \
  --configfile "<caminho-do-nuget.config-de-usuario>"
```

**Opção B — variável de ambiente** (sem gravar em disco):

```bash
# bash
export NuGetPackageSourceCredentials_github-fcg="Username=<seu-usuario-github>;Password=<SEU_PAT_read:packages>"
```
```powershell
# PowerShell
$env:NuGetPackageSourceCredentials_github-fcg = "Username=<seu-usuario-github>;Password=<SEU_PAT_read:packages>"
```

> **Atenção:** mantenha token, senha ou credencial **fora** do `nuget.config` versionado e de
> qualquer arquivo rastreado.

## Build e testes locais

Com o token configurado:

```bash
# restaura e compila a solution inteira
dotnet restore
dotnet build

# testes (unit + integração)
dotnet test
```

> Os testes de **integração** sobem PostgreSQL e RabbitMQ reais via **Testcontainers** — é preciso
> ter um runtime de containers (Docker) disponível na máquina.

## Docker

O `dotnet restore` ocorre **dentro** do build da imagem, então o token do `Fcg.Contracts` entra
via **secret mount do BuildKit** (não fica em nenhuma layer da imagem final):

```bash
DOCKER_BUILDKIT=1 docker build \
  --secret id=gh_token,src=<arquivo-com-o-PAT> \
  -t fcg-payments .
```

> `src` aponta para um **arquivo** contendo apenas o PAT (`read:packages`). No Linux/macOS dá para
> usar `src=<(echo -n "$SEU_PAT")`.

### Rodando o container

O serviço lê a configuração de variáveis de ambiente (chaves aninhadas usam `__`):

```bash
docker run --rm -p 8080:8080 \
  -e ConnectionStrings__Payments="Host=postgres;Database=payments;Username=fcg;Password=fcg" \
  -e RabbitMq__Host="rabbitmq" \
  -e RabbitMq__Username="guest" \
  -e RabbitMq__Password="guest" \
  fcg-payments
```

> Sem a connection string `Payments` o serviço **falha no startup** (fail-fast), por design — não
> sobe pela metade.

### Variáveis de ambiente

| Variável | Obrigatória | Descrição |
| :--- | :--- | :--- |
| `ConnectionStrings__Payments` | sim | Conexão do PostgreSQL (pagamentos) |
| `RabbitMq__Host` | sim | Host do RabbitMQ |
| `RabbitMq__Port` | não | Porta do RabbitMQ (default 5672) |
| `RabbitMq__Username` / `RabbitMq__Password` | sim | Credenciais do RabbitMQ |
| `Payment__RejectionThreshold` | não | Limite de autorização da simulação (default 5000) |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | não | Endpoint OTLP — só então traces/métricas são exportados |
| `Loki__Url` | não | URL do Loki — só então o sink Loki é ligado |

## Migração (Job de bootstrap)

**A mesma imagem** serve a API-health e o Job de bootstrap: a flag `--migrate` é um argumento de
runtime lido antes de subir o host web. Ao terminar, o processo **retorna sem subir o host**. Boot
normal (sem a flag) não migra.

```bash
# aplica as migrations e encerra
docker run --rm \
  -e ConnectionStrings__Payments="Host=postgres;Database=payments;Username=fcg;Password=fcg" \
  fcg-payments --migrate
```

No Kubernetes isso vira um `Job` (`payments-migrate`) que reusa a imagem com
`command: ["dotnet", "Fcg.Payments.Api.dll", "--migrate"]`.

## Observabilidade

Logs no **console** e enricher de `TraceId`/`SpanId` estão **sempre** ativos. Os sinks de rede são
**opcionais e desacoplados** — entram apenas se o endpoint correspondente estiver configurado:

- **Loki** (logs): ligado só com `Loki__Url`. O identificador do serviço é o label de stream
  `app=fcg-payments`.
- **OTLP** (traces/métricas → Tempo/Prometheus): ligado só com `OTEL_EXPORTER_OTLP_ENDPOINT`. O
  MassTransit entra como source/meter e propaga o `TraceId` via headers AMQP — este serviço é o
  **elo do meio** do trace da saga: o consume de `order-placed` encadeia ao publish do catalog, e
  o publish de `payment-processed` encadeia adiante.

Sem esses endpoints o serviço **sobe limpo**, console-only, sem erros de conexão. O *service name*
reportado é **`Fcg.Payments.Api`**.

## Health checks

| Endpoint | Significado |
| :--- | :--- |
| `GET /health/live` | Liveness — processo vivo (não checa dependências). |
| `GET /health/ready` | Readiness — reflete **apenas o PostgreSQL** (dependência dura). |
| `GET /health` | Agregado (informativo). |

O broker (RabbitMQ) **não** entra no `/health/ready`: o **Outbox** desacopla o veredito da entrega
ao broker (se o RabbitMQ cai, o consumo para mas o `PaymentProcessedEvent` pendente fica seguro na
`outbox_message`), então derrubar a readiness por causa dele reiniciaria o pod à toa.

## Imagem no GHCR

A imagem é publicada em **`ghcr.io/reinaldogez/fcg-payments`** (tags `latest` + `{sha}`).

## Deploy

Os manifestos **Kubernetes** deste serviço **não vivem aqui**: estão centralizados no repositório
de orquestração **`fcg-ops`** (Deployment/Service/ConfigMap/Secret + o `Job` de migrate).
