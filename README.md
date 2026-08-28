# BookRent — API de Empréstimos Concorrentes e Auditáveis

API REST em **.NET 10 LTS** para o catálogo e os empréstimos de uma biblioteca, projetada
para rodar em **múltiplas réplicas** sem perder integridade: duas requisições simultâneas
nunca podem emprestar o mesmo último exemplar.

---

## Como rodar

**Pré-requisito:** Docker Desktop / Engine com Compose v2. Só isso.

```bash
git clone <repo> && cd book-rent-new
docker compose up -d --build
```

Isso levanta seis containers e espera PostgreSQL e Redis ficarem *healthy* antes de subir
a API, que aplica as migrations no startup. Verificação rápida:

```bash
curl http://localhost:8080/health/ready
```

Deve responder `200` com os checks `postgres` e `redis` saudáveis. A documentação
interativa fica em **http://localhost:8080/scalar/**.

Um passeio completo pela API, do zero ao empréstimo:

```bash
# 1. cadastra um livro com 1 exemplar
BOOK=$(curl -s -X POST http://localhost:8080/books \
  -H 'Content-Type: application/json' -H 'X-Actor-Id: bibliotecaria' \
  -d '{"title":"Dom Casmurro","isbn":"978-85-359-1066-3","author":"Machado de Assis","totalCopies":1}' \
  | grep -o '"id":"[^"]*' | cut -d'"' -f4)

# 2. cadastra um leitor
USER=$(curl -s -X POST http://localhost:8080/users \
  -H 'Content-Type: application/json' \
  -d '{"name":"Maria Silva","email":"maria@exemplo.com"}' \
  | grep -o '"id":"[^"]*' | cut -d'"' -f4)

# 3. consulta a disponibilidade (leitura cacheada no Redis)
curl -s "http://localhost:8080/books/$BOOK/availability"

# 4. empresta o único exemplar — Idempotency-Key é obrigatória
curl -s -X POST http://localhost:8080/loans \
  -H 'Content-Type: application/json' -H 'Idempotency-Key: chave-001' -H 'X-Actor-Id: maria' \
  -d "{\"bookId\":\"$BOOK\",\"userId\":\"$USER\"}"

# 5. repita EXATAMENTE o comando 4: devolve o mesmo empréstimo, sem criar outro
#    (veja o cabeçalho de resposta Idempotency-Replayed: true)

# 6. tente emprestar com outra chave: 409 loan.no_copies_available
curl -s -X POST http://localhost:8080/loans \
  -H 'Content-Type: application/json' -H 'Idempotency-Key: chave-002' \
  -d "{\"bookId\":\"$BOOK\",\"userId\":\"$USER\"}"

# 7. a trilha de auditoria registrou tudo
curl -s "http://localhost:8080/audit-events?entityType=Book&entityId=$BOOK"
```

Parar tudo (`-v` também apaga os volumes de dados):

```bash
docker compose down
docker compose down -v
```

### Se alguma porta estiver ocupada

Copie `.env.example` para `.env` e ajuste. O Compose lê esse arquivo automaticamente e o
`.env` está no `.gitignore`.

```bash
cp .env.example .env
```

```dotenv
POSTGRES_PORT=5442
REDIS_PORT=6389
API_PORT=8085
JAEGER_UI_PORT=16687
PROMETHEUS_PORT=9091
```

Todas as portas publicadas são parametrizáveis — inclusive as de observabilidade —,
então dá para rodar duas stacks do projeto lado a lado.

> Em máquinas com um PostgreSQL nativo instalado, a porta 5432 costuma já estar tomada —
> e o container fica na sombra dele sem erro aparente. Se `docker compose up` funcionar mas
> a conexão falhar com autenticação, é esse o caso: mude `POSTGRES_PORT`.

### Rodando fora do container

```bash
docker compose up -d postgres redis
dotnet run --project src/BookRent.Api
```

Sobe em http://localhost:5080 com `ASPNETCORE_ENVIRONMENT=Development`, apontando para
`localhost` e aplicando as migrations no startup. Requer .NET SDK 10.0.100+.

---

## Testes

```bash
dotnet test BookRent.slnx                       # tudo
dotnet test tests/BookRent.UnitTests            # rápidos, sem I/O
dotnet test tests/BookRent.IntegrationTests     # exige Docker
```

**103 testes: 59 unitários e 44 de integração.** Os de integração **não** usam banco
in-memory — sobem PostgreSQL 17 e Redis 8 reais via Testcontainers, porque os cenários de
concorrência dependem do comportamento específico do PostgreSQL. Um teste verde sobre uma
semântica que não existe em produção é o pior tipo de teste verde.

Cobertura dos cenários que o desafio exige:

| Cenário | Onde |
| --- | --- |
| Último exemplar sob concorrência (2, 8 e 20 requisições simultâneas) | `ConcurrencyTests` |
| Idempotência: mesma chave, chave com corpo diferente, chaves simultâneas | `ConcurrencyTests` |
| Histórico preservado após devolução, cancelamento e desativação | `ConcurrencyTests` |
| Conflito otimista no `PATCH` e ausência de conflito falso | `ConcurrencyTests` |
| Cache populado na leitura e invalidado na escrita | `CatalogEndpointsTests` |
| Trilha de auditoria append-only | `CatalogEndpointsTests` |
| Regras de negócio do domínio | `BookTests`, `LoanTests`, `UserTests` |
| Regra de dependência entre camadas | `LayerDependencyTests` |

> **Honestidade sobre o teste de corrida:** ele é probabilístico. Passar não *prova*
> ausência de condição de corrida — só mostra que naquela execução não houve. Por isso as
> asserções são sobre o **estado final do banco**, nunca sobre ordem ou tempo. A garantia
> real vem do `UPDATE` condicional e da `CHECK` constraint; o teste é evidência.

---

## Migrations

Moram em `src/BookRent.Infrastructure/Persistence/Migrations` e rodam com
`src/BookRent.Api` como *startup project*.

```bash
dotnet tool install --global dotnet-ef

# aplicar
dotnet ef database update \
  --project src/BookRent.Infrastructure --startup-project src/BookRent.Api

# criar uma nova
dotnet ef migrations add NomeDaMigration \
  --project src/BookRent.Infrastructure --startup-project src/BookRent.Api \
  --output-dir Persistence/Migrations

# script idempotente para revisão ou pipeline
dotnet ef migrations script --idempotent \
  --project src/BookRent.Infrastructure --startup-project src/BookRent.Api
```

| Ambiente | Quem aplica |
| --- | --- |
| Compose / desenvolvimento | `Database__MigrateOnStartup=true` — uma réplica só, conveniente |
| Kubernetes / produção | `Database__MigrateOnStartup=false` + `Job` dedicado antes do rollout |

Migrar no startup com N réplicas coloca N processos disputando o lock de migration e
acopla o schema ao ciclo de vida do pod. Por isso o padrão em `appsettings.json` é `false`.

---

## Variáveis de ambiente

Toda configuração vem de `appsettings.*.json` sobrescrito por variáveis de ambiente
(separador `__`). **Nenhum segredo é versionado:** os valores em `appsettings.json` são
placeholders e os do Compose valem só para a stack local descartável.

| Variável | Padrão | Descrição |
| --- | --- | --- |
| `ConnectionStrings__Postgres` | — | **Obrigatória.** Connection string do PostgreSQL |
| `ConnectionStrings__Redis` | — | **Obrigatória.** Endpoint do Redis |
| `Database__MigrateOnStartup` | `false` | Aplica migrations pendentes ao subir |
| `Cache__DefaultTtl` | `00:05:00` | TTL padrão do cache |
| `Cache__InstanceName` | `bookrent:` | Prefixo das chaves no Redis |
| `Loans__DefaultLoanPeriodDays` | `14` | Prazo de devolução |
| `Loans__IdempotencyRetention` | `1.00:00:00` | Por quanto tempo uma `Idempotency-Key` vale para replay. Formato `TimeSpan`: use `d.hh:mm:ss` — `"24:00:00"` seria lido como **24 dias**, não 24 horas |
| `ASPNETCORE_ENVIRONMENT` | `Production` | `Development` habilita Scalar e OpenAPI |
| `ASPNETCORE_HTTP_PORTS` | `8080` | Porta do Kestrel |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | — | Vazio desliga a exportação de telemetria |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `grpc` | `grpc` ou `http/protobuf` |
| `OTEL_SERVICE_NAME` | `bookrent-api` | Nome do serviço na telemetria |

**Atenção ao `Maximum Pool Size` dentro da connection string.** O pool do Npgsql é *por
processo*: com o `maxReplicas: 11` do HPA e o padrão original de 40, o teto seria 440
conexões contra as ~97 úteis de um `max_connections=100`. Está em **8**
(`11 × 8 = 88`). Em Kubernetes a connection string vem do `Secret` e **sobrescreve a do
`appsettings.json` por inteiro** — o limite precisa estar lá, e o exemplo em
`deploy/k8s/secret.example.yaml` já o traz com a conta ao lado.

---

## Endpoints

| Método | Rota | Notas |
| --- | --- | --- |
| `POST` | `/books` | 201 + `Location` |
| `GET` | `/books?q=&page=&pageSize=&includeInactive=` | Busca por título ou autor |
| `GET` | `/books/{id}` | Cacheado |
| `PATCH` | `/books/{id}` | Concorrência otimista via `expectedVersion` |
| `DELETE` | `/books/{id}` | **Desativa**, nunca apaga |
| `POST` | `/users` | 201 + `Location` |
| `GET` | `/users/{id}` | |
| `GET` | `/users/{id}/loans?status=` | Histórico do leitor |
| `POST` | `/loans` | **Exige `Idempotency-Key`** |
| `POST` | `/loans/{id}/return` | |
| `POST` | `/loans/{id}/cancel` | |
| `GET` | `/books/{id}/availability` | Cacheado |
| `GET` | `/books/{id}/history` | Histórico do livro |
| `GET` | `/audit-events?entityType=&entityId=&action=&actor=&correlationId=&from=&to=` | Trilha de negócio |
| `GET` | `/health/live` · `/health/ready` | |

**Cabeçalhos:** `X-Correlation-Id` (gerado se ausente, devolvido na resposta),
`X-Actor-Id` (ator registrado na auditoria), `Idempotency-Key` (obrigatório em `POST /loans`).

Erros seguem **Problem Details** com a extension `code` — o contrato estável, não a
mensagem — e `correlationId`. Status por categoria: **404** recurso inexistente, **409**
conflito de estado, **422** semântica inválida do payload, **400** requisição malformada.
A distinção entre 409 e 422 importa: ela diz ao cliente se vale a pena repetir.

### Ajustes ao contrato sugerido

O desafio permite ajustar o contrato mediante documentação. Foram três:

1. **`DELETE /books/{id}` desativa, nunca apaga**, e recusa com 409 se houver empréstimo
   **ativo**. O enunciado oferece "desativar **ou** rejeitar a remoção" — fazemos os dois,
   conforme o estado. Histórico encerrado não impede a desativação e sobrevive a ela.
2. **O replay idempotente devolve 201**, a resposta originalmente produzida, mais o
   cabeçalho `Idempotency-Replayed: true`. Devolver 409 contradiria o propósito da
   idempotência; o enunciado pede "a resposta previamente produzida".
3. **`GET /users/{id}` entrou** fora da lista mínima, porque `POST /users` devolve
   `Location` apontando para ele — um `Location` que responde 404 seria contrato quebrado.
   `GET /books` também ganhou paginação e busca.

---

## Concorrência

> **Estratégia: atualização condicional atômica**, no `POST /loans`.

```sql
UPDATE bookrent.books
   SET available_copies = available_copies - 1
 WHERE id = @bookId AND is_active AND available_copies > 0;
```

`rows_affected = 0` **já é a resposta de negócio** — 409 `loan.no_copies_available` — e não
um erro técnico a interpretar. Uma releitura acontece só no caminho de erro, para separar
livro inexistente (404) de desativado (409) e sem exemplar (409).

**Por que é correto.** O comando avalia o predicado e escreve no mesmo passo: não existe
janela entre ler e decidir. Sob `READ COMMITTED`, duas transações que tocam a mesma linha
serializam no *row lock*; a segunda espera a primeira commitar e o PostgreSQL **reavalia o
`WHERE` contra a versão nova** da linha (`EvalPlanQual`). Com um exemplar, ela encontra
`available_copies = 0` e afeta zero linhas.

A `CHECK (available_copies >= 0 AND <= total_copies)` é a rede: nem um bug futuro na
aplicação grava quantidade negativa. A garantia não depende de o código estar certo.

### Trade-offs considerados

| Alternativa | Por que não |
| --- | --- |
| `SELECT ... FOR UPDATE` e depois `UPDATE` | Um round-trip a mais e lock segurado por mais tempo, sem ganho — o `UPDATE` condicional já dá a mesma garantia em um comando |
| Isolamento `REPEATABLE READ` / `SERIALIZABLE` | Correto, mas a segunda requisição receberia `40001` (falha técnica a capturar e reexecutar) em vez de um 409 limpo. Exigiria laço de retry em todo caminho de escrita. **Nossa estratégia depende de `READ COMMITTED`**: o `EvalPlanQual` só existe nele |
| Token otimista na disponibilidade | Gera tempestade de retry justamente no ponto contendido — o último exemplar é onde o conflito é a regra, não a exceção. E a resolução ingênua do conflito produz *lost update* |
| Lock distribuído no Redis | Segunda fonte de verdade, mais expiração de lock. O PostgreSQL já dá exclusão mútua correta **e durável no mesmo commit** |
| `rowversion` | Recurso do SQL Server; o desafio veta. O equivalente aqui seria `xmin` — ver abaixo por que também não |
| `BookCopy` como entidade + `SKIP LOCKED` | Mais fiel ao domínio (cada exemplar teria código de barras) e espalharia a contenção. Recusado por proporcionalidade: nenhum endpoint pede a identidade do exemplar, e exemplar já emprestado não poderia ser apagado, exigindo um estado "aposentado" que o contador dispensa |

### Duas estratégias, de propósito

O critério não é gosto, é a natureza da escrita:

> **Escrita relativa** (`SET x = x - 1`) não precisa de token: a aritmética acontece no
> banco, sobre o valor corrente, dentro do lock.
> **Escrita absoluta** (`SET title = 'X'`) precisa: o valor foi calculado a partir de uma
> leitura que pode ter envelhecido.

| Caminho | Escrita | Mecanismo | Conflito é |
| --- | --- | --- | --- |
| `POST /loans`, `return`, `cancel` | Relativa (contador) | `UPDATE` condicional atômico | A regra: alto |
| `PATCH /books/{id}` | Absoluta (descritivos) | Token `Version` + 409, sem retry | A exceção: baixo |

**O token cobre apenas os campos descritivos.** Se fosse `xmin` — que muda a cada `UPDATE`
na linha, inclusive os que só mexem no contador — um bibliotecário editando um livro
movimentado levaria 409 atrás de 409 por causa de empréstimos sem nenhuma relação com a
edição dele. Empréstimo, devolução e cancelamento **não incrementam `Version`**. Há teste
unitário e de integração guardando isso.

Dentro do `PATCH`, a quantidade também é escrita de forma relativa e condicional:

```sql
UPDATE bookrent.books
   SET total_copies = @novo,
       available_copies = available_copies + @delta,
       version = @versaoLida + 1
 WHERE id = @id AND version = @versaoLida AND available_copies + @delta >= 0;
```

Zero linhas não diz qual condição falhou; uma releitura separa **409** (alguém alterou) de
**422** (reduziria o acervo abaixo dos empréstimos ativos). A condição `+ @delta >= 0` é
avaliada pelo banco contra o valor **corrente**, então ela sozinha impede a redução
indevida, mesmo que um empréstimo tenha entrado no meio.

**Sem retry automático no `PATCH`:** repetir sozinho significaria mesclar duas edições
humanas, e mesclagem automática perde intenção. O conflito volta para quem edita decidir.

---

## Idempotência

`POST /loans` exige `Idempotency-Key`; ausente → **400**.

A chave vira uma linha em `idempotency_records`, com PK composta `(endpoint, key)`,
reservada com `INSERT ... ON CONFLICT DO NOTHING` **na mesma transação do empréstimo**.

- **Reservou** → executa o caso de uso e grava a resposta produzida no próprio registro.
  Empréstimo, auditoria e resposta nascem no **mesmo commit**.
- **Não reservou** → a chave é de outra requisição. Se ela ainda não commitou, o `INSERT`
  **bloqueia no índice único** até terminar: **o próprio PostgreSQL é o mutex**, sem lock
  distribuído. Depois, com o mesmo `request_hash`, devolve a resposta armazenada (201 +
  `Idempotency-Replayed: true`); com hash diferente, **422** — mesma chave para corpo
  diferente é erro do cliente, não replay.

Duas consequências que os testes provam: quatro requisições **simultâneas** com a mesma
chave produzem **um** empréstimo, e uma requisição que **falha** faz rollback e **libera a
chave** em vez de queimá-la.

SQL explícito porque o change tracker não expressa `ON CONFLICT`, e capturar a violação
não serve no PostgreSQL: qualquer erro aborta a transação inteira, e recuperar exigiria
`SAVEPOINT`.

**Devolução e cancelamento não exigem chave.** O `UPDATE ... WHERE status = 'Active'` já os
torna idempotentes por construção — a segunda chamada afeta zero linhas e responde 409. E o
exemplar só volta à circulação **depois** da transição confirmada; na ordem inversa, uma
devolução repetida incrementaria a disponibilidade duas vezes.

---

## Auditoria

Tabela `audit_events`, **append-only**, gravada **na mesma transação da mudança** — a
trilha não pode divergir do fato. Cada evento registra entidade, identificador, ação, ator,
timestamp em UTC, `correlationId` e um `jsonb` com o suficiente para entender a mudança
(no `book.updated`, os valores antes e depois).

Ações: `book.created`, `book.updated`, `book.deactivated`, `loan.created`, `loan.returned`,
`loan.cancelled`. Consultáveis em `GET /audit-events` com filtros combináveis.

**Chamada explícita no caso de uso**, e não um interceptor do `SaveChanges`. O interceptor
seria automático e impossível de esquecer, mas registraria *mudança de linha*, não
*intenção de negócio*: "`available_copies` foi de 1 para 0" não é a mesma informação que
"empréstimo criado por fulano". O desafio pede trilha de negócio e diz que logs técnicos
não a substituem.

**Custo assumido:** cada caso de uso precisa lembrar de auditar, e esquecer é silencioso.
Mitigado por testes de integração que exigem o evento de cada operação.

O `DbContext` bloqueia `UPDATE` e `DELETE` em `AuditEvent`. **Limite conhecido:** isso
protege o change tracker, não o banco — `ExecuteUpdate`, `ExecuteDelete` ou SQL cru passam
por cima. Em produção, a garantia definitiva é revogar essas permissões no PostgreSQL.

---

## Cache

**Uma única chave**, `bookrent:book:{id}`, TTL 60 s, guardando o snapshot completo do livro
e servindo **dois** endpoints: `GET /books/{id}` devolve o snapshot inteiro e
`GET /books/{id}/availability` projeta os números do mesmo objeto. Os dois leem
literalmente os mesmos bytes e não conseguem divergir.

*Cache-aside*: a leitura popula, a escrita **invalida com `DEL`**, sempre **depois do
commit**. Gatilhos: criação, `PATCH`, `DELETE`, empréstimo, devolução e cancelamento.

**Por que uma chave com o livro todo, e não só a disponibilidade:** no *miss*, as duas
opções fazem o mesmo `SELECT` e recebem a linha inteira. Cachear só os números seria
descartar dados que já vieram. *Cacheie na granularidade que a fonte produz.*

**Por que `DEL` e não atualizar o valor:** o lock do banco termina no `COMMIT` e não ordena
nada depois disso — duas réplicas que commitaram em ordem podem alcançar o Redis na ordem
inversa. `DEL` é idempotente e comutativo; `SET` não. **`DEL` converge para a verdade;
`SET` pode convergir para uma mentira plausível.**

**Por que a listagem `GET /books` não é cacheada**, apesar de ser a consulta mais cara:
enquanto a resposta trouxer disponibilidade, todo empréstimo invalidaria todas as páginas e
a taxa de acerto ficaria próxima de zero. E o gargalo real da busca é índice, não cache —
há índices **GIN com `pg_trgm`** em `title` e `author`, porque `ILIKE '%termo%'` não
aproveita B-tree. Índice deixa rápido sem criar obrigação de coerência permanente.

**O PostgreSQL é sempre a autoridade.** O cache nunca participa da decisão de emprestar. Um
cache defasado pode mostrar um número errado numa tela; nunca pode causar um empréstimo
errado. Falha de Redis é degradação, não erro: o `RedisCacheStore` registra log e devolve o
controle ao banco.

**Custos assumidos:** existe a janela de corrida clássica (um leitor pode gravar o valor
antigo depois da invalidação) e não há proteção contra *stampede*. O **TTL é rede de
segurança**, não só política de frescor: se o processo morrer entre o `COMMIT` e o `DEL`,
60 s é o tempo máximo de divergência.

---

## Observabilidade

**Logs** — Serilog, JSON compacto em produção. Todo log carrega `CorrelationId` e `Actor`.
Sem cabeçalho de correlação, o middleware usa o `TraceId` do span corrente, mantendo log e
trace alinhados, e devolve o valor na resposta.

**Traces** — ASP.NET Core, `HttpClient` e Npgsql, mais a `ActivitySource` própria. Em
http://localhost:16686 (Jaeger).

**Métricas de negócio**, em http://localhost:9090 (Prometheus):

| Instrumento | Significado |
| --- | --- |
| `bookrent.loans.created` | Empréstimos criados |
| `bookrent.loans.rejected` (tag `reason`) | Rejeições, com o código da regra |
| `bookrent.loans.idempotent_replays` | Requisições que reaproveitaram resposta |
| `bookrent.loans.request.duration` (tag `outcome`) | Latência do endpoint de empréstimo |

**Health checks** — a separação é deliberada:

| Endpoint | Verifica | Com PostgreSQL/Redis fora |
| --- | --- | --- |
| `/health/live` | Só o processo | Continua `200` |
| `/health/ready` | PostgreSQL e Redis | Responde `503` |

Se o *liveness* checasse dependências, uma queda do banco reiniciaria pods saudáveis em
cascata, transformando indisponibilidade parcial em total.

---

## Kubernetes: correto entre 2 e 11 réplicas

Manifests em `deploy/k8s/`: `Deployment` (3 réplicas, requests/limits, liveness/readiness/
startup probes, container não-root com filesystem somente leitura), `Service`, `HPA` (2–11),
`ConfigMap`, `Secret` de exemplo **sem valores reais** e `Job` de migrations.

**A corretude é indiferente ao número de réplicas — e o motivo é preciso:** o banco é um só,
e **o lock mora nele, não na aplicação**. Duas requisições disputando o último exemplar
podem cair no mesmo pod, em pods diferentes ou em zonas diferentes; as duas viram
transações no mesmo PostgreSQL, competindo pelo mesmo *row lock*. Subir de 2 para 11 não
cria um problema novo: adiciona clientes a um gerenciador de locks que já resolve N
clientes por construção.

1. **Sem estado no processo.** Nada em memória local, nenhuma afinidade de sessão.
2. **PostgreSQL como única fonte de verdade** para disponibilidade e criação de empréstimo.
3. **Cache é otimização, não autoridade** — falha vira degradação.
4. **Escrita idempotente** persistida no banco: um retry que caia em outra réplica
   reencontra a resposta.
5. **Migrations fora do ciclo de vida do pod** — `Job` dedicado.
6. **Rollout sem janela de erro** — `maxUnavailable: 0` mais readiness real.

**O que quebraria:** apenas **multi-primário / active-active**, onde dois primários
decrementariam cada um a sua cópia da linha. Réplicas de leitura não quebram o caminho
crítico (a disponibilidade é decidida pelo `UPDATE`, no primário); *failover* produz falha,
não inconsistência, porque tudo está num commit só.

**A capacidade, essa não é indiferente** — ver a nota sobre `Maximum Pool Size` acima.

---

## Arquitetura

Monólito modular em Clean Architecture. **A distribuição é por réplicas, não por
serviços**: o desafio exige que N instâncias cheguem ao mesmo resultado, o que se resolve
no PostgreSQL. Fatiar catálogo e empréstimos em serviços separados tornaria impossível
garantir "exatamente um empréstimo bem-sucedido" com uma transação local — introduziria
consistência eventual justamente onde ela é inaceitável.

```
BookRent.Api              apresentação / composition root
        ├──────────────► BookRent.Infrastructure   EF Core, Redis, health checks
        └──────────────► BookRent.Application ◄────┘   casos de uso e portas
                                 ▼
                          BookRent.Domain            entidades e invariantes
```

A regra de dependência aponta sempre para dentro, e não fica só na convenção:
`LayerDependencyTests` quebra o build se alguém adicionar uma referência proibida.

Decisões estruturais, com o raciocínio completo em
[`docs/plano-implementacao.md`](docs/plano-implementacao.md): sem MediatR (indireção sem
problema a resolver nesta escala); sem FluentValidation (o domínio é dono da validação, em
qualquer caminho de entrada); Minimal APIs; `Guid` v7 nas entidades expostas — id
sequencial em API sem autenticação é enumerável — e `bigint identity` na auditoria, que
nunca vai em URL nem é chave estrangeira.

O `docs/plano-implementacao.md` registra **cada decisão** no formato *escolha /
alternativas / custo assumido / o que inverteria a escolha*, incluindo as que foram
recusadas e por quê.

---

## Limitações conhecidas

- **Sem autenticação.** O ator vem de `X-Actor-Id`, autodeclarado. Suficiente para a trilha
  de auditoria do desafio; inadequado como fronteira de segurança ou escopo de idempotência.
- **`GET /audit-events` sem restrição de acesso.** Em produção, endpoint privilegiado.
- **Sem expurgo de `idempotency_records`.** O `ExpiresAt` está gravado, mas quem apaga seria
  um `Job` agendado.
- **Janela de corrida do cache e ausência de proteção contra *stampede*** — mitigadas por
  TTL curto, sem impacto em corretude.
- **Paginação por offset**, não por cursor: adequada ao volume, degrada em tabelas grandes e
  pode repetir um item entre páginas se houver inserção concorrente.
- **Bloqueio append-only da auditoria vale só no change tracker**, não no banco.
- **Imagem do `Job` de migrations** ainda precisa ser construída a partir de um
  `dotnet ef migrations bundle`.
- **ISBN validado por formato, não por dígito verificador.**
- **Teste de concorrência é evidência, não prova.**

## Como eu evoluiria para produção

1. **Autenticação e autorização** — ator vindo do token, `GET /audit-events` restrito, e a
   `Idempotency-Key` escopada por cliente, o que elimina colisão entre clientes distintos.
2. **PgBouncer em modo *transaction*** — hoje o pool é dimensionado pelo teto de réplicas
   (`11 × 8`); um pooler externo permitiria pools maiores multiplexados em poucas conexões
   reais, que é a resposta certa quando o número de réplicas cresce.
3. **Expurgo das chaves de idempotência** por `CronJob`, e particionamento de
   `audit_events` por data quando o volume justificar.
4. **`ETag` + `If-Match` no `PATCH`**, substituindo o `expectedVersion` no corpo pela
   semântica HTTP canônica de edição condicional.
5. **Revogar `UPDATE`/`DELETE` em `audit_events`** para o usuário da aplicação, tornando a
   trilha imutável no banco e não só no código. Com exigência de conformidade,
   encadeamento por hash para tornar adulteração detectável.
6. **Paginação por cursor** nas listagens que crescem, e substituir o `COUNT` exato por
   `hasNext` onde o total não for necessário.
7. **`BookCopy` como entidade**, se a identidade do exemplar passar a importar (código de
   barras, estado de conservação, filiais) ou se a contenção num único título virar real. A
   migração é aditiva e não muda o contrato HTTP.
8. **Alertas sobre as métricas de negócio** — taxa de rejeição por indisponibilidade e
   latência do endpoint de empréstimo são os dois sinais que antecipam problema real.
