# BookRent — API de Empréstimos Concorrentes e Auditáveis

API REST em **.NET 10 LTS** para o catálogo e os empréstimos de uma biblioteca, projetada
para rodar em **múltiplas réplicas** sem perder integridade: duas requisições simultâneas
nunca podem emprestar o mesmo último exemplar.

> **Estado atual: boilerplate.** A infraestrutura está montada e validada ponta a ponta
> (build, containers, health checks, telemetria, testes). O modelo de domínio e os endpoints
> de negócio ainda **não** foram implementados — ver [O que ainda falta](#o-que-ainda-falta).

---

## Stack

| Camada | Tecnologia |
| --- | --- |
| Runtime | .NET 10 LTS / ASP.NET Core Minimal APIs |
| Banco (fonte de verdade) | PostgreSQL 17 + EF Core 10 (provider Npgsql) |
| Cache | Redis 8 |
| Logs | Serilog (JSON estruturado em produção) |
| Traces e métricas | OpenTelemetry → OTLP → Collector → Jaeger / Prometheus |
| Documentação da API | OpenAPI nativo do ASP.NET Core + Scalar |
| Testes | xUnit v3, Shouldly, NSubstitute, Testcontainers |
| Empacotamento | Dockerfile multi-stage + Docker Compose + manifests Kubernetes |

---

## Arquitetura

Monólito modular organizado em Clean Architecture. **A distribuição do sistema é por
réplicas, não por serviços**: o desafio exige que N instâncias do mesmo processo cheguem ao
mesmo resultado, o que se resolve no PostgreSQL — não fatiando o domínio em microserviços,
o que introduziria consistência eventual justamente onde ela não é aceitável.

```
BookRent.Api              apresentação / composition root
        │                 endpoints, middlewares, DI, telemetria, health
        ├──────────────► BookRent.Infrastructure
        │                 EF Core/PostgreSQL, Redis, health checks, migrations
        │                        │
        └──────────────► BookRent.Application ◄────┘
                          casos de uso e portas (interfaces)
                                 │
                                 ▼
                          BookRent.Domain
                          entidades, invariantes e regras — sem dependências
```

A regra de dependência aponta **sempre para dentro**. Ela não fica só na convenção:
`tests/BookRent.UnitTests/Architecture/LayerDependencyTests.cs` quebra o build se alguém
adicionar uma referência proibida.

```
.
├── BookRent.slnx                  solução no formato XML (.slnx)
├── Directory.Build.props          TFM, nullable e analisadores de todos os projetos
├── Directory.Packages.props       versões centralizadas dos pacotes (CPM)
├── docker-compose.yml             stack local completa
├── deploy/
│   ├── k8s/                       Deployment, Service, HPA, ConfigMap, Secret, Job
│   ├── otel/                      configuração do OpenTelemetry Collector
│   └── prometheus/                configuração de scrape
├── src/
│   ├── BookRent.Domain/
│   ├── BookRent.Application/
│   ├── BookRent.Infrastructure/
│   └── BookRent.Api/              inclui o Dockerfile
└── tests/
    ├── BookRent.UnitTests/
    └── BookRent.IntegrationTests/ Testcontainers (PostgreSQL e Redis reais)
```

---

## Pré-requisitos

| Ferramenta | Versão | Necessário para |
| --- | --- | --- |
| Docker Desktop / Engine | com Compose v2 | subir a stack e rodar os testes de integração |
| .NET SDK | 10.0.100+ | compilar, testar e rodar migrations fora do container |
| `dotnet-ef` | 10.x | criar e aplicar migrations |

O `global.json` fixa o SDK em 10.0.1xx com `rollForward: latestFeature`.
Para rodar apenas via Docker Compose, **só o Docker é necessário**.

Instalar a ferramenta do EF Core:

```bash
dotnet tool install --global dotnet-ef
```

---

## Subindo a stack

```bash
docker compose up -d --build
```

Isso levanta seis containers e espera PostgreSQL e Redis ficarem *healthy* antes de iniciar a API.

| Serviço | URL / porta | Para quê |
| --- | --- | --- |
| API | http://localhost:8080 | a aplicação |
| Documentação (Scalar) | http://localhost:8080/scalar/ | apenas em `Development` |
| OpenAPI JSON | http://localhost:8080/openapi/v1.json | apenas em `Development` |
| PostgreSQL | `localhost:5432` | usuário/senha/base `bookrent` (só desenvolvimento) |
| Redis | `localhost:6379` | — |
| Jaeger (traces) | http://localhost:16686 | traces da API |
| Prometheus (métricas) | http://localhost:9090 | métricas da API |
| OTLP do Collector | `localhost:4317` (gRPC), `4318` (HTTP) | destino da telemetria |

Verificação rápida:

```bash
curl http://localhost:8080/health/live
curl http://localhost:8080/health/ready
```

`/health/ready` deve responder `200` com os checks `postgres` e `redis` saudáveis.

Parar tudo (`-v` também apaga os volumes de dados):

```bash
docker compose down
docker compose down -v
```

### Portas ocupadas

Se alguma porta já estiver em uso na sua máquina, copie `.env.example` para `.env` e ajuste —
o Compose lê esse arquivo automaticamente e o `.env` está no `.gitignore`:

```bash
cp .env.example .env
```

```dotenv
POSTGRES_PORT=5442
REDIS_PORT=6389
API_PORT=8085
```

### Rodando várias réplicas

```bash
docker compose up -d --scale api=3
```

Para isso, remova o mapeamento fixo `ports` do serviço `api` (ou troque por uma faixa, por
exemplo `"8080-8082:8080"`), já que várias réplicas não podem publicar a mesma porta do host.

---

## Rodando fora do container

Suba só as dependências e execute a API pelo SDK:

```bash
docker compose up -d postgres redis
dotnet run --project src/BookRent.Api
```

A API sobe em http://localhost:5080 com `ASPNETCORE_ENVIRONMENT=Development`, que já aponta
para `localhost:5432` e `localhost:6379` e aplica as migrations no startup.

---

## Migrations

As migrations moram em `src/BookRent.Infrastructure` (projeto do `DbContext`) e são
executadas com `src/BookRent.Api` como *startup project*.

```bash
# criar uma migration
dotnet ef migrations add NomeDaMigration \
  --project src/BookRent.Infrastructure \
  --startup-project src/BookRent.Api

# aplicar no banco apontado pela configuração corrente
dotnet ef database update \
  --project src/BookRent.Infrastructure \
  --startup-project src/BookRent.Api

# gerar script idempotente para revisão / pipeline
dotnet ef migrations script --idempotent \
  --project src/BookRent.Infrastructure \
  --startup-project src/BookRent.Api \
  --output artifacts/migrations.sql
```

`DesignTimeDbContextFactory` permite rodar os comandos apontando só para o projeto de
infraestrutura; nesse caso a connection string vem da variável `ConnectionStrings__Postgres`.

**Quem aplica as migrations, e quando:**

| Ambiente | Estratégia |
| --- | --- |
| Compose / desenvolvimento | `Database__MigrateOnStartup=true` — uma réplica só, conveniente |
| Kubernetes / produção | `Database__MigrateOnStartup=false` + `Job` dedicado antes do rollout |

Migrar no startup com N réplicas coloca N processos disputando o lock de migration e acopla
o schema ao ciclo de vida do pod. Por isso o padrão em `appsettings.json` é `false`.

---

## Testes

```bash
# tudo
dotnet test BookRent.slnx

# só unitários (rápidos, sem I/O)
dotnet test tests/BookRent.UnitTests

# só integração (exige Docker: sobe PostgreSQL e Redis via Testcontainers)
dotnet test tests/BookRent.IntegrationTests
```

Os testes de integração **não** usam banco in-memory: os cenários de concorrência dependem do
comportamento real do PostgreSQL. `BookRentApiFactory` sobe os containers, injeta as
connection strings e hospeda a API com a pipeline HTTP completa; os containers são
compartilhados por toda a coleção de testes para não subir um par por classe.

Estado atual: **6 testes passando** (3 unitários e 3 de integração), mais 3 marcados como
`Skip` — justamente os cenários centrais do desafio, aguardando o domínio.

---

## Configuração

Toda configuração vem de `appsettings.*.json` sobrescrito por variáveis de ambiente
(separador `__`). **Nenhum segredo é versionado**: os valores em `appsettings.json` são
placeholders e os de desenvolvimento local valem apenas para o Compose.

| Variável | Padrão | Descrição |
| --- | --- | --- |
| `ConnectionStrings__Postgres` | — | connection string do PostgreSQL (obrigatória) |
| `ConnectionStrings__Redis` | — | endpoint do Redis (obrigatória) |
| `Database__MigrateOnStartup` | `false` | aplica migrations pendentes ao subir |
| `Cache__DefaultTtl` | `00:05:00` | TTL padrão das entradas de cache |
| `Cache__InstanceName` | `bookrent:` | prefixo das chaves no Redis |
| `ASPNETCORE_ENVIRONMENT` | `Production` | `Development` habilita Scalar e OpenAPI |
| `ASPNETCORE_HTTP_PORTS` | `8080` | porta HTTP do Kestrel |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | — | vazio desliga a exportação de telemetria |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `grpc` | `grpc` ou `http/protobuf` |
| `OTEL_SERVICE_NAME` | `bookrent-api` | nome do serviço na telemetria |

---

## Observabilidade

**Logs** — Serilog com `CompactJsonFormatter` em produção e template legível em
desenvolvimento. Todo log carrega `CorrelationId` e `Actor`, injetados pelo
`CorrelationIdMiddleware` a partir dos cabeçalhos `X-Correlation-Id` e `X-Actor-Id`.
Quando o cliente não envia correlação, o middleware usa o `TraceId` do span corrente,
mantendo log e trace alinhados, e devolve o valor no cabeçalho da resposta.

**Traces** — instrumentação de ASP.NET Core, `HttpClient` e Npgsql, mais a `ActivitySource`
própria (`BookRent.Api`) para os casos de uso. Visíveis em http://localhost:16686.

**Métricas** — além das métricas de runtime e de HTTP, a classe `LoanMetrics` já declara os
instrumentos de negócio exigidos pelo desafio:

| Instrumento | Tipo | Significado |
| --- | --- | --- |
| `bookrent.loans.created` | contador | empréstimos criados com sucesso |
| `bookrent.loans.rejected` | contador (tag `reason`) | rejeições por regra de negócio |
| `bookrent.loans.idempotent_replays` | contador | requisições que reaproveitaram resposta |
| `bookrent.loans.request.duration` | histograma | latência do endpoint de empréstimo |

**Health checks** — a separação é deliberada:

| Endpoint | Verifica | Comportamento com PostgreSQL/Redis fora |
| --- | --- | --- |
| `GET /health/live` | apenas o processo | continua `200` |
| `GET /health/ready` | PostgreSQL e Redis | responde `503` |

Se o *liveness* também checasse dependências, uma queda do banco reiniciaria em cascata pods
perfeitamente saudáveis, transformando indisponibilidade parcial em total.

---

## Kubernetes

Manifests em `deploy/k8s/`:

| Arquivo | Conteúdo |
| --- | --- |
| `namespace.yaml` | namespace `bookrent` |
| `configmap.yaml` | configuração não sensível |
| `secret.example.yaml` | **estrutura** do Secret, sem valores reais |
| `deployment.yaml` | 3 réplicas, requests/limits, liveness/readiness/startup probes, container não-root com filesystem somente leitura |
| `service.yaml` | `ClusterIP` na porta 80 |
| `hpa.yaml` | autoscaling entre **2 e 11 réplicas** por CPU |
| `migration-job.yaml` | `Job` que aplica as migrations antes do rollout |

Não é necessário provisionar cluster; os manifests fazem parte da entrega.

### Por que a aplicação continua correta entre 2 e 11 réplicas

1. **Sem estado no processo.** Nada é guardado em memória local: não há afinidade de sessão
   nem cache local que possa divergir entre pods.
2. **PostgreSQL como única fonte de verdade.** Disponibilidade e criação de empréstimo são
   decididas no banco, dentro de transação — nunca a partir do Redis.
3. **Cache é otimização, não autoridade.** `RedisCacheStore` trata falha de cache como
   degradação: registra log e devolve o controle para o banco.
4. **Escrita idempotente.** O `Idempotency-Key` é persistido no PostgreSQL, então um retry
   que caia em outra réplica reencontra a resposta já produzida.
5. **Migrations fora do ciclo de vida do pod.** `Job` dedicado, não N pods migrando juntos.
6. **Rollout sem janela de erro.** `maxUnavailable: 0` mais readiness real: o pod só entra no
   Service depois que PostgreSQL e Redis respondem.

> Os itens 2 e 4 dependem do domínio, ainda não implementado — as decisões estão tomadas e a
> infraestrutura está preparada para elas.

---

## Decisões já tomadas no boilerplate

- **Monólito modular, não microserviços.** O desafio pede corretude sob concorrência entre
  réplicas; fatiar catálogo e empréstimos em serviços separados tornaria impossível garantir
  "exatamente um empréstimo bem-sucedido" com uma transação local.
- **Solução em `.slnx`.** Formato XML de solução, suportado nativamente pelo SDK do .NET 10.
- **Central Package Management.** Todas as versões em `Directory.Packages.props`, sem drift
  entre projetos.
- **`TreatWarningsAsErrors` ligado**, com analisadores em `latest-recommended`. As poucas
  regras relaxadas estão no `.editorconfig`, cada uma com a justificativa ao lado.
- **Logs de caminho quente via source generator** (`[LoggerMessage]`): sem boxing e sem
  alocação quando o nível está desligado.
- **Connection strings resolvidas tardiamente** (na construção do serviço, não no registro),
  para que overrides de teste e de orquestrador sejam respeitados.
- **`TimeProvider` em vez de `DateTime.UtcNow`** quando o domínio precisar de relógio —
  testável sem truques.
- **Imagem final Alpine com usuário não-root** e restore em camada separada, para
  aproveitamento de cache.

---

## O que ainda falta

Este repositório entrega o esqueleto. O próximo passo é o domínio e os casos de uso:

- [ ] Entidades: `Book`, `BookCopy`, `User`, `Loan`, `AuditEvent`, `IdempotencyRecord`
- [ ] Migration inicial
- [ ] Endpoints de catálogo, usuários, empréstimos, disponibilidade, histórico e auditoria
- [ ] Controle de concorrência do último exemplar (atualização condicional atômica no
      PostgreSQL, com token de concorrência — `xmin` do Npgsql, **não** `rowversion`, que é
      específico do SQL Server) e o teste de integração concorrente
- [ ] Idempotência do `POST /loans` via `Idempotency-Key` persistido
- [ ] Trilha de auditoria com ator, timestamp UTC e `correlationId`
- [ ] Cache de disponibilidade/catálogo com invalidação coerente na escrita
- [ ] Preenchimento das seções de decisões de concorrência, idempotência, cache e auditoria
      com a estratégia efetivamente implementada e seus trade-offs

### Limitações conhecidas do estado atual

- Não há autenticação: o ator vem do cabeçalho `X-Actor-Id`, suficiente para a trilha de
  auditoria do desafio, insuficiente para produção.
- O `docker-compose.yml` usa credenciais de desenvolvimento em texto claro, apropriadas
  apenas para a stack local descartável.
- Jaeger e Prometheus rodam sem persistência (retenção de 2 h no Prometheus).
- O `Job` de migrations referencia uma imagem (`bookrent-migrations:local`) que ainda precisa
  ser construída a partir de um `dotnet ef migrations bundle`.
