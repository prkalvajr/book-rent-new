# Plano de implementação — decisões, alternativas e trade-offs

Documento de trabalho, para revisão antes de escrever código, e para servir de base na
conversa com o avaliador. Cobre modelo de dados, concorrência, idempotência, auditoria,
cache, contrato dos endpoints, testes e ordem de execução.

**Toda decisão aqui segue o mesmo formato**, para que possa ser comparada e defendida:

> **Escolha** — o que faço.
> **Alternativas** — o que mais resolveria, e por que não escolhi.
> **Custo assumido** — o que perco escolhendo isso. Nenhuma decisão é de graça.
> **Mudaria se** — a condição concreta que inverteria a escolha.

O índice de todas as decisões está na [§10](#10-índice-de-decisões). As seções §1–§7
tratam do que ainda será implementado; a [§9](#9-decisões-já-tomadas-no-boilerplate)
cobre o que já está no repositório e vai ser questionado do mesmo jeito.

---

## 1. Modelo de dados

### 1.1 Contador no `Book`, não `BookCopy` como entidade

O desafio diz *"uma biblioteca possui livros e vários exemplares de cada livro"* e, ao
mesmo tempo, identifica o livro por *"título, ISBN, autor e **quantidade de exemplares**"*.
As duas leituras cabem no texto, e a escolha muda a estratégia de concorrência da §2.

> **Escolha: contador no `Book`.** `TotalCopies` e `AvailableCopies`; o empréstimo
> referencia o livro, não um exemplar.

| 									   | **A — contador no `Book`** *(escolhida)* 							   | **B — `BookCopy` individual** |
| --- 					  			   | --- 																	   | --- |
| Concorrência 			  			   | `UPDATE ... WHERE available_copies > 0` — um comando, sem `SELECT` prévio | `SELECT ... FOR UPDATE SKIP LOCKED LIMIT 1` — pega qualquer exemplar livre 					  |
| Contenção 			  			   | Todos os empréstimos do **mesmo livro** serializam **na mesma linha**     | `SKIP LOCKED` espalha a disputa entre as linhas de exemplar: throughput maior num título popular |
| Invariante "sem quantidade negativa" | `CHECK (available_copies >= 0)` — o banco garante 						   | Emerge do estado das linhas; não há número para ficar negativo 							      |
| `GET /availability` 				   | Leitura de uma coluna 													   | `COUNT` com filtro (mais caro — e é justamente por isso que o cache da §5 ganha mais valor aqui) |
| `PATCH` de quantidade 			   | Ajusta dois inteiros no mesmo commit 									   | Insere/remove linhas, e remover exige escolher exemplares não emprestados 						  |
| Identidade do exemplar 			   | Impossível dizer *qual* exemplar foi emprestado 						   | Natural — abre caminho para código de barras, estado de conservação, filial 					  |
| Custo de implementação 			   | 3 tabelas de negócio 													   | 4 tabelas, mais um `join` em quase toda leitura 												  |

**B é o modelo mais fiel ao domínio, e isso fica registrado:** numa biblioteca real cada
exemplar é um objeto físico com código de barras próprio. A escolha por A é de
**proporcionalidade ao escopo**, não uma afirmação de que B seria excessivo.

**Por que A mesmo assim.** Nenhum endpoint do contrato sugerido pede a identidade do
exemplar — `POST /loans` recebe `bookId`, `GET /books/{id}/availability` devolve um número.
O enunciado fala em *"nenhuma **quantidade** negativa"* (a expressão pressupõe que existe
uma quantidade), lista *"atualização condicional atômica"* como primeira estratégia
aceitável, e pede explicitamente para priorizar *"corretude, clareza e testes em vez de
complexidade de arquitetura"*.

**Custos de B que pesaram na conta**, além do `join`:

- **Exemplar precisa de estado "aposentado".** Com FK `RESTRICT` de `loans` para
  `book_copies`, um exemplar já emprestado não pode ser apagado. Reduzir a quantidade exige
  um status `Retired` e transforma `availability` em `COUNT(*) WHERE status='Available'`.
  Com contador, é aritmética.
- **Reduzir quantidade deixa de ser `total -= n`:** vira escolher *quais* exemplares
  aposentar, só entre os disponíveis, verificando se havia quantos bastam.
- **`loans` precisa de `book_id` denormalizado** ao lado de `copy_id`, senão todo histórico
  por livro ganha mais um hop — e é um dado duplicado a manter coerente.
- **`POST /books` vira 1 + N inserts**, e a tabela passa a crescer com o acervo, não com o
  catálogo.

**Dois pontos em que B seria melhor, recusados conscientemente:** (1) com `BookCopy` o
empréstimo não escreve na linha de `books`, então o conflito falso da §9.7 desapareceria e
o `xmin` bastaria como token; (2) um índice único parcial
`(copy_id) WHERE status='Active'` expressaria a invariante central — "um exemplar não pode
estar em dois empréstimos ativos" — de forma mais direta que `CHECK (available_copies >= 0)`.

**Custo assumido de A:** a linha do livro vira ponto quente — todos os empréstimos
simultâneos de um mesmo título se enfileiram nela. Irrelevante no volume do desafio;
gargalo num best-seller com centenas de requisições por segundo.

**Mudaria se:** o domínio passasse a precisar da identidade do exemplar (código de barras,
estado de conservação, acervo distribuído entre filiais), **ou** se a contenção num único
título se tornasse real. **Não é porta de mão única:** a migração para B é aditiva — cria a
tabela, faz backfill a partir do contador, troca as leituras. O contrato HTTP não muda.

### 1.2 Entidades

```
Book                                    ← aggregate root
  Id                Guid                UUIDv7 (ver §9.6)
  Title             string(300)
  Isbn              string(20)          UNIQUE (normalizado: só dígitos e X)
  Author            string(200)
  TotalCopies       int                 CHECK >= 0
  AvailableCopies   int                 CHECK >= 0 AND <= TotalCopies
  IsActive          bool                soft delete
  CreatedAt         DateTimeOffset
  UpdatedAt         DateTimeOffset?
  DeactivatedAt     DateTimeOffset?
  Version           int                 token do PATCH; so o PATCH incrementa (ver §9.7)

User                                    ← aggregate root
  Id                Guid
  Name              string(200)
  Email             string(320)         UNIQUE (lowercase)
  CreatedAt         DateTimeOffset

Loan                                    ← aggregate root
  Id                Guid
  BookId            Guid → Book         ON DELETE RESTRICT
  UserId            Guid → User         ON DELETE RESTRICT
  Status            LoanStatus          Active | Returned | Cancelled
  LoanedAt          DateTimeOffset
  DueAt             DateTimeOffset      LoanedAt + Loans:DefaultLoanPeriodDays (14)
  ReturnedAt        DateTimeOffset?
  CancelledAt       DateTimeOffset?
  Actor             string(200)         quem originou (X-Actor-Id)

AuditEvent                              ← append-only: nunca UPDATE, nunca DELETE
  Id                bigint identity     nao vai em URL nem e FK (ver §9.6)
  EntityType, EntityId, Action, Actor,
  OccurredAt (timestamptz UTC), CorrelationId, Data (jsonb)

IdempotencyRecord                       ← PK COMPOSTA (Endpoint, Key)
  Endpoint          string(100)         escopo da chave
  Key               string(200)         valor do cabecalho Idempotency-Key
  RequestHash       char(64)            SHA-256 do corpo canonico
  ResponseStatus    int?                nulos ate a transacao concluir
  ResponseBody      jsonb?
  LoanId            Guid?               ponteiro de diagnostico, sem FK
  CreatedAt, ExpiresAt  DateTimeOffset
```

**Por que a PK é composta, e não só a `Key`:** ela escopa a chave ao endpoint, então dois
endpoints podem receber a mesma string de um cliente ingênuo sem colidir. E é exatamente
este índice único que serve de **mutex** entre requisições concorrentes (§3) — a segunda
bloqueia nele até a primeira commitar.

Índices, além de PKs e uniques: `loans(book_id, loaned_at desc)`,
`loans(user_id, loaned_at desc)`, `loans(book_id) WHERE status = 'Active'` (parcial, para
a checagem do `DELETE`), `audit_events(entity_type, entity_id, occurred_at desc)`,
`audit_events(occurred_at desc)`, `idempotency_records(expires_at)`.

**Índice de busca textual para `GET /books?q=`:** a busca por título/autor usa
`ILIKE '%termo%'`, que **não** aproveita índice B-tree — o padrão começa com curinga, então
o PostgreSQL varre a tabela. A solução é um índice GIN com a extensão `pg_trgm`
(trigramas), que indexa `ILIKE` com curinga nos dois lados:

```sql
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE INDEX ix_books_title_trgm  ON bookrent.books USING gin (title  gin_trgm_ops);
CREATE INDEX ix_books_author_trgm ON bookrent.books USING gin (author gin_trgm_ops);
```

Registrado aqui porque é a resposta certa para a listagem ser a consulta mais cara —
**índice, não cache** (§5). Um índice deixa a consulta rápida e não cria obrigação
nenhuma de coerência; um cache deixa rápido e cria uma obrigação permanente de invalidar.
Como a extensão precisa existir no banco, a migration inicial deve criá-la (o usuário da
aplicação precisa de permissão, ou a extensão é provisionada fora — vai como nota de
operação).

### 1.3 Status como `enum` mapeado para texto

> **Escolha:** `LoanStatus` como `enum` no domínio, persistido como **texto** no
> PostgreSQL (`'Active'`, `'Returned'`, `'Cancelled'`).

| Alternativa 								  | Ganho 										  | Por que não |
| --- 		  	 							  | ---   										  | --- 		|
| `int` no banco 							  | 4 bytes, comparação mais rápida 			  | Dump do banco vira ilegível; renumerar o `enum` corrompe dados silenciosamente |
| `enum` nativo do PostgreSQL (`CREATE TYPE`) | Validação no próprio banco, compacto 		  | Adicionar um valor exige migration com `ALTER TYPE`, e o mapeamento Npgsql precisa de registro global no `DataSource` — cerimônia desproporcional |

**Custo assumido:** texto ocupa mais espaço e um `UPDATE` errado poderia gravar um valor
que o `enum` do C# não reconhece. Mitigado por `CHECK (status IN (...))` na migration.

**Mudaria se:** a tabela crescesse a ponto de o tamanho da coluna importar — aí `enum`
nativo do PostgreSQL, que junta legibilidade e compactação.

---

## 2. Concorrência — o coração do desafio

### 2.1 Estratégia: atualização condicional atômica

> **Escolha:** decrementar a disponibilidade com **um único comando condicional**, dentro
> da transação do empréstimo, e decidir pelo número de linhas afetadas.

```sql
UPDATE bookrent.books
   SET available_copies = available_copies - 1
 WHERE id = @bookId
   AND is_active = true
   AND available_copies > 0;
```

`rows_affected = 0` → `DomainException("loan.no_copies_available")` → **409** com Problem
Details. `= 1` → insere `Loan` + `AuditEvent` e commita.

**Por que é correto:** o comando avalia o predicado e escreve no mesmo passo — não existe
janela entre ler e decidir. Sob `READ COMMITTED`, duas transações que tocam a mesma linha
serializam no *row lock*; a segunda espera a primeira commitar e o PostgreSQL então
**reavalia o `WHERE` contra a versão nova** da linha (`EvalPlanQual`). Com um exemplar, a
segunda encontra `available_copies = 0`, o predicado falha e ela afeta zero linhas.
Exatamente o resultado exigido: um empréstimo, uma recusa clara, nenhum estado inválido.

A `CHECK (available_copies >= 0)` é a rede de segurança: mesmo um bug futuro na aplicação
não consegue gravar quantidade negativa. A garantia não depende do código estar certo.

### 2.2 Alternativas

| Alternativa 										    | Ganho 													     | Por que não |
| --- 													| --- 														     | --- |
| `SELECT ... FOR UPDATE` e depois `UPDATE` 			| Permite ler o estado antes de decidir, código mais explícito   | Um round-trip a mais e lock segurado por mais tempo, sem ganho de corretude — o `UPDATE` condicional já dá a mesma garantia num comando |
| Isolamento `SERIALIZABLE` 							| Garantia geral, dispensa raciocinar sobre cada caminho 		 | Exige laço de retry para `40001` em toda a aplicação e custa mais sob carga. Prefiro o isolamento mais barato que resolve o problema, e raciocinar explicitamente sobre ele |
| Token otimista na disponibilidade (estender o `Version` do `PATCH` ao contador) 			| Uniformiza a estratégia com a do `PATCH` 					     | Gera tempestade de retry justamente no ponto contendido — o último exemplar. Otimismo é a escolha errada onde o conflito é a regra, não a exceção. Ver §2.4 para o *lost update* que a resolução ingênua produz |
| `SELECT ... FOR UPDATE SKIP LOCKED` sobre exemplares  | Espalha a contenção, throughput bem maior num título disputado | Depende da opção B da §1.1 (`BookCopy` como entidade), descartada por proporcionalidade ao escopo. Seria a estratégia se voltássemos a ela |
| Lock distribuído no Redis 							| Tira a disputa do banco 										 | Segunda fonte de verdade, mais o problema clássico de expiração de lock. O PostgreSQL já oferece exclusão mútua correta *e durável no mesmo commit* |
| Fila serializando empréstimos 						| Elimina a disputa por construção 								 | Transforma uma operação síncrona em assíncrona: o cliente deixaria de receber "emprestado" ou "indisponível" na própria resposta. Muda o contrato para resolver um problema que o banco resolve |

**Custo assumido:** `ExecuteUpdateAsync` não passa pelo *change tracker* — a instância de
`Book` eventualmente carregada em memória fica desatualizada depois do decremento. O caso
de uso precisa não salvar estado obsoleto por cima. É um risco real e vou tratá-lo não
carregando a entidade nesse caminho: o comando condicional é a única escrita em `books`.

**Mudaria se:** a operação precisasse decidir sobre **mais de um agregado** ao mesmo tempo
(ex.: limite de empréstimos por usuário *e* disponibilidade, cada um numa linha diferente)
— aí o `UPDATE` condicional isolado não bastaria e eu subiria para `SERIALIZABLE` com
retry, ou ordenaria os locks explicitamente para evitar deadlock.

### 2.3 Devolução e cancelamento

> **Escolha:** mesmo padrão condicional, guardado pelo status atual.

```sql
UPDATE bookrent.loans SET status = 'Returned', returned_at = @now
 WHERE id = @loanId AND status = 'Active';
```

Zero linhas → o empréstimo já foi devolvido ou cancelado → 409 `loan.not_active`. Só
depois de `rows_affected = 1` o `available_copies` é incrementado, no mesmo commit.

**Ganho colateral:** devolução e cancelamento ficam idempotentes **por construção** — uma
segunda chamada não tem efeito e responde 409. Por isso não exigem `Idempotency-Key`.

**Custo assumido:** o cliente não distingue "esse empréstimo nunca esteve ativo" de "você
já devolveu": os dois dão 409. Aceito — a resposta carrega o status atual no corpo, o que
resolve na prática.

**Devolução e cancelamento não passam pelo token de concorrência.** São escritas
relativas (`available_copies + 1`), como o empréstimo. Nenhum
`DbUpdateConcurrencyException`, nenhum 409 porque outra pessoa mexeu no livro no meio.

### 2.4 Duas estratégias de propósito: otimista no `PATCH`, pessimista no empréstimo

A escolha não é de gosto nem de padronização — é por caminho, e o critério é objetivo:

> **Escrita absoluta** (`SET title = 'X'`, `SET total_copies = 7`) precisa de token, porque
> o valor foi calculado a partir de uma leitura que pode ter envelhecido.
> **Escrita relativa** (`SET available_copies = available_copies - 1`) não precisa, porque
> a aritmética acontece no banco, sobre o valor corrente, dentro do lock da linha.

Tokens de concorrência existem para proteger escritas absolutas. Contadores querem
escritas relativas. Daí a regra que vale em todo o projeto:

- **`available_copies` nunca é escrito de forma absoluta, em lugar nenhum** — sempre
  `= available_copies ± n`, sempre com condição no `WHERE`.
- **O token protege apenas os campos descritivos** do livro, onde a escrita é absoluta.

| | `POST /loans`, `return`, `cancel` | `PATCH /books/{id}` |
| --- | --- | --- |
| Natureza da escrita | Relativa (contador) | Absoluta (campos descritivos) |
| Frequência de conflito | Alta — o último exemplar é onde o conflito é a regra | Baixa — edição humana, esporádica |
| Mecanismo | `UPDATE` condicional atômico | Token de concorrência + `DbUpdateConcurrencyException` |
| Falha significa | Regra de negócio: sem exemplar → 409 `loan.no_copies_available` | Conflito de edição → 409 `book.concurrent_modification` |
| Retry | Não existe: a falha **é** a resposta | Não automático — devolvido ao cliente (ver §6.5) |

**Por que o token otimista não serve para o empréstimo.** Funciona — o desafio inclusive
lista "token de concorrência" como estratégia aceitável, e não seria errado. Mas tem três
problemas concretos:

**(a) A armadilha do *lost update* na resolução do conflito.** Livro com 5 exemplares,
duas requisições simultâneas:

```
A lê:     available=5, version=100
B lê:     available=5, version=100
A grava:  SET available=4 WHERE version=100   → ok, version vira 101
B grava:  SET available=4 WHERE version=100   → 0 linhas → DbUpdateConcurrencyException
```

O token fez o trabalho dele: **detectou**. Mas se o conflito for resolvido como no exemplo
da documentação oficial do EF Core — `entry.OriginalValues.SetValues(databaseValues)` e
tentar de novo — o `CurrentValues` de B continua sendo `4`:

```
B grava:  SET available=4 WHERE version=101   → sucesso
```

**Resultado: 4. O correto era 3.** Dois empréstimos aconteceram e um sumiu da contagem: o
token detectou o conflito e a resolução ingênua jogou a detecção fora. O conserto é
recalcular no retry (`valorDoBanco - 1`) e reavaliar a regra — factível, mas é código que
precisa estar certo. O `SET available = available - 1 WHERE available > 0` é **impossível**
de errar assim.

**(b) Conflitos falsos.** Um token de linha inteira (como o `xmin`) faria uma edição de
título disparar conflito num empréstimo simultâneo, sem nenhuma relação com
disponibilidade. É a razão de o token ser restrito aos campos descritivos (§9.7).

**(c) Tempestade de retry no ponto errado.** Otimismo aposta que o conflito é raro. O
último exemplar é, por definição, onde o conflito é **certo**. É onde a aposta perde.

**Nota sobre o EF Core:** `ExecuteUpdateAsync` não participa do mecanismo de token — não
lança `DbUpdateConcurrencyException`, devolve o número de linhas afetadas e a decisão é
nossa. É exatamente o que a §2.1 faz.

### 2.5 Por que `READ COMMITTED`, e não um isolamento mais alto

A estratégia da §2.1 **depende** de `READ COMMITTED`. É nele que o PostgreSQL, ao liberar
o *row lock*, reavalia o `WHERE` contra a versão nova da linha (`EvalPlanQual`) — o que
transforma a disputa pelo último exemplar num `rows_affected = 0` limpo.

Sob **`REPEATABLE READ`**, a transação inteira enxerga um único *snapshot* tirado no
primeiro comando. Não há reavaliação: ao tentar alterar uma linha que outra transação
commitou depois do snapshot, o PostgreSQL **aborta a transação** com
`40001 serialization_failure`. Na prática é concorrência otimista automática no nível da
transação, sem token nenhum.

| Nível | O que a segunda requisição pelo último exemplar recebe |
| --- | --- |
| `READ COMMITTED` *(escolhido)* | `rows_affected = 0` → **409 de regra de negócio**, direto |
| `REPEATABLE READ` / `SERIALIZABLE` | `40001` → falha técnica que precisa ser capturada e reexecutada para só então descobrir que não há exemplar |

**Custo de subir o isolamento:** laço de retry em todo caminho de escrita, mais peças e
uma mensagem pior, para chegar ao mesmo resultado. **Ganho que abriríamos mão em
`READ COMMITTED`:** leituras repetíveis dentro da transação — que não precisamos, porque
nenhuma decisão nossa depende de ler duas vezes o mesmo dado na mesma transação.

**Mudaria se:** uma decisão passasse a depender de várias leituras coerentes entre si
dentro da mesma transação.

---

## 3. Idempotência do `POST /loans`

`Idempotency-Key` **obrigatório**; ausente → 400 (`loan.idempotency_key_required`).

> **Escolha:** tabela `idempotency_records` com PK na chave, gravada **na mesma transação
> do empréstimo**, usando o índice único do PostgreSQL como mecanismo de exclusão mútua.

1. `INSERT INTO idempotency_records (...) ON CONFLICT (key) DO NOTHING`
2. **Inseriu** → esta requisição é a dona da chave. Executa o caso de uso, grava
   `response_status`/`response_body` no próprio registro e commita. Empréstimo e registro
   nascem no **mesmo commit**: ou existem os dois, ou nenhum.
3. **Não inseriu** → outra requisição já tem a chave.
   - Se a concorrente ainda não commitou, o `INSERT` **bloqueia no índice único** até ela
     terminar. O próprio PostgreSQL é o mutex.
   - `request_hash` igual → devolve a resposta armazenada com o status original (**201**),
     mais o cabeçalho `Idempotency-Replayed: true`, e incrementa
     `bookrent.loans.idempotent_replays`.
   - `request_hash` diferente → **422** `loan.idempotency_key_reused`: mesma chave para
     corpo diferente é erro do cliente, não replay.

Se a requisição **falhar**, o rollback libera a chave — ela não fica queimada por uma
tentativa que não produziu efeito nenhum.

### Alternativas

| Alternativa | Ganho | Por que não |
| --- | --- | --- |
| Coluna `idempotency_key UNIQUE` na própria tabela `loans` | **Bem mais simples**: uma tabela a menos, e o unique já garante o empréstimo único | Não guarda a resposta produzida (só dá para reconstruí-la relendo o empréstimo), não detecta reuso da chave com corpo diferente, e não serve a nenhum outro endpoint. É a alternativa mais forte — se o escopo fosse só "não duplicar", eu escolheria esta |
| `SETNX` no Redis | Rápido, tira carga do banco | Segunda fonte de verdade. Se o Redis perder a chave (eviction, restart sem AOF), a garantia evapora — e ela não commita junto com o empréstimo, então existe janela de inconsistência |
| Estado explícito `in_progress` + polling | Permite responder 409 "em andamento" em vez de bloquear | Desnecessário: o `INSERT` não commitado já bloqueia o concorrente pelo tempo exato da transação. Um estado a mais para manter e para deixar preso em caso de crash |
| Deduplicação só por corpo (hash), sem chave | Cliente não precisa gerar chave | Dois empréstimos legítimos e idênticos (mesmo usuário pegando o mesmo livro duas vezes) seriam confundidos. O desafio pede o cabeçalho |

**Custo assumido:**
- Uma tabela e uma escrita a mais no caminho quente de todo empréstimo.
- Requisições concorrentes com a **mesma** chave ficam bloqueadas até a primeira commitar.
  É o comportamento correto, mas prende uma conexão do pool — sob abuso, vira pressão no
  pool. Um `lock_timeout` na transação limitaria o dano.
- Sem expurgo automático: `ExpiresAt` está gravado, mas quem apaga seria um `Job`
  agendado, fora do escopo. Vai como limitação conhecida.

**Escopo da chave:** unicidade por `(endpoint, key)`, não global — dois endpoints
diferentes podem receber a mesma string de um cliente ingênuo sem colidir. Não escopo por
ator porque não há autenticação: o `X-Actor-Id` é autodeclarado e não serve de fronteira
de segurança.

**Mudaria se:** houvesse autenticação real — aí a chave seria escopada por cliente, o que
elimina colisão entre clientes distintos e permite quota por chave.

---

## 4. Auditoria

> **Escolha:** porta `IAuditTrail` na Application, adaptador em Infrastructure, chamada
> **explicitamente pelo caso de uso, na mesma transação da mudança**.

Ator e `CorrelationId` vêm do `ICorrelationContext` (já existente, alimentado pelo
`CorrelationIdMiddleware`). `OccurredAt` em UTC via `TimeProvider` injetado. `Data`
(jsonb) carrega o suficiente para entender a mudança: em `book.updated`, os campos
alterados com valor antes e depois; em `loan.created`, `bookId`/`userId`/`dueAt`.

Eventos: `book.created`, `book.updated`, `book.deactivated`, `loan.created`,
`loan.returned`, `loan.cancelled` — os cinco mínimos exigidos, mais alteração de livro.

### Alternativas

| Alternativa | Ganho | Por que não |
| --- | --- | --- |
| Interceptor do `SaveChanges` lendo o `ChangeTracker` | Automático e impossível de esquecer; cobre 100% das escritas | Registra **mudança de linha**, não **intenção de negócio**. "`available_copies` foi de 1 para 0" não é a mesma informação que "empréstimo criado por fulano". O desafio pede trilha de negócio explicitamente |
| Eventos de domínio despachados no `SaveChanges` | Desacopla o caso de uso do registro; testável isoladamente | Maquinaria (coleção de eventos na entidade, dispatcher, handlers) que só se paga com muitos assinantes. Aqui há um só: a tabela |
| Outbox + consumidor assíncrono | Necessário se a trilha fosse para outro sistema | Adiciona um componente e latência sem resolver nada: a trilha vive na mesma base e commita junto com o fato |
| Banco separado para auditoria | Isola retenção, volume e permissões | **Quebra a atomicidade**: a trilha poderia divergir do fato num crash entre os dois commits. Inaceitável para o que o desafio pede |
| Só logs estruturados | Zero código novo | O desafio veta: *"logs técnicos não a substituem"* |

**Custo assumido:** cada caso de uso precisa lembrar de auditar, e o esquecimento é
silencioso — nada quebra, o evento simplesmente não existe. Mitigação: um teste de
integração por operação afirmando que o evento correspondente foi gravado. É o preço de
registrar intenção em vez de diff.

`GET /audit-events` com filtros `entityType`, `entityId`, `action`, `from`, `to`, `actor`,
`correlationId` e paginação. Tabela append-only: sem `UPDATE`, sem `DELETE`, e o
`DbContext` bloqueia os dois.

**Mudaria se:** a exigência virasse conformidade/retenção longa (LGPD, auditoria externa)
— aí entraria assinatura/encadeamento por hash para tornar adulteração detectável, e
particionamento por data.

---

## 5. Cache Redis

> **Escolha:** *cache-aside* (leitura popula, escrita **invalida**), sobre a porta
> `ICacheStore` já existente. **Uma única chave** — `bookrent:book:{id}`, TTL 60 s —
> guardando o snapshot completo do livro e servindo **dois** endpoints.

```json
// bookrent:book:{id}
{ "id": "...", "title": "...", "author": "...", "isbn": "...",
  "isActive": true, "totalCopies": 5, "availableCopies": 3 }
```

- `GET /books/{id}` devolve o snapshot inteiro.
- `GET /books/{id}/availability` lê **a mesma chave** e projeta `totalCopies`,
  `availableCopies` e `available` no seu próprio DTO.

Um só caminho de leitura, compartilhado: tenta o Redis; no *miss*, um `SELECT` na linha do
livro, grava o snapshot e devolve. Os dois endpoints não conseguem divergir, porque leem
literalmente os mesmos bytes.

O prefixo `bookrent:` vem do `InstanceName` do `IDistributedCache` (`Cache:InstanceName`),
aplicado pelo adaptador — `CacheKeys` devolve só a parte lógica (`book:{id}`). Um prefixo
também em `CacheKeys` produziria `bookrent:bookrent:book:{id}`. O TTL vem de
`Cache:DefaultTtl`, e não de constante no código, para que a variável documentada tenha
efeito de verdade.

**Por que uma chave com tudo, e não uma chave só com a disponibilidade:** o *miss* faz o
mesmo `SELECT` nos dois desenhos e recebe a linha inteira de qualquer forma. Cachear só os
dois números significaria descartar título, autor e ISBN que já vieram, para buscá-los de
novo no `GET /books/{id}`. Guardar a linha toda é de graça. A regra que generaliza:
**cacheie na granularidade que a fonte produz, não na que o endpoint consome.**

Invalidação **sempre depois do commit**, e sempre `DEL` — nunca `SET` do valor novo:

| Operação | Efeito no banco | No Redis, após o commit |
| --- | --- | --- |
| `POST /loans` | `available_copies − 1` | `DEL bookrent:book:{id}` |
| `POST /loans/{id}/return` · `cancel` | `available_copies + 1` | `DEL bookrent:book:{id}` |
| `PATCH /books/{id}` | descritivo e/ou quantidade | `DEL bookrent:book:{id}` |
| `DELETE /books/{id}` | `is_active = false` | `DEL bookrent:book:{id}` |

Repare na assimetria proposital: no banco a operação é relativa (`− 1`), no cache é um
`DEL`. **Não decrementamos o valor cacheado.** A ordem em que as réplicas alcançam o Redis
não é a ordem em que commitaram no PostgreSQL — o lock do banco termina no `COMMIT` e não
governa efeito colateral nenhum depois disso. `DEL` é idempotente e comutativo, então dá o
mesmo resultado em qualquer ordem; `SET` não, e a última escrita a chegar pode ser a mais
velha.

Por que depois do commit: invalidar dentro de uma transação que depois faz rollback
apagaria uma entrada ainda válida (inofensivo — o pior caso é um *miss*), mas **popular**
dentro da transação publicaria estado não commitado (grave). Há ainda um motivo de
desempenho: o *row lock* do livro é segurado até o commit, então um `DEL` antes dele
colocaria um Redis lento no caminho crítico e enfileiraria todos os empréstimos daquele
título atrás de uma chamada de cache.

### 5.1 Por que `GET /books` não é cacheada — resumo

A listagem é a consulta **mais cara** do sistema, então a decisão de deixá-la fora do cache
é a que mais precisa ser defendida. Em cinco linhas:

| Motivo | Detalhe |
| --- | --- |
| **Taxa de acerto próxima de zero** | Enquanto a resposta trouxer disponibilidade, **todo empréstimo invalida a listagem inteira**. O cache é descartado antes de ser reaproveitado: paga-se escrita e memória sem colher acerto |
| **Explosão de chaves com filtros** | A chave depende de `page`, `pageSize`, `q` e `includeInactive`. Com `q` livre o espaço de chaves é ilimitado; a maioria das entradas seria escrita uma vez e nunca lida, pressionando o `maxmemory 256mb` com `allkeys-lru` e evictando entradas úteis |
| **Invalidação em leque** | Criar um livro desloca a fronteira de todas as páginas; editar um título muda em que página ele cai e se ele casa com um dado `q`. Qualquer escrita afeta potencialmente **toda** entrada cacheada |
| **Sem `DEL` por prefixo** | O `IDistributedCache` não oferece. Seria preciso um contador de geração na chave (`books:v{N}:...` + `INCR` a cada escrita) — tecnicamente resolvido, mas não conserta os três motivos acima |
| **O gargalo real é índice** | A busca `q` é lenta por falta de índice GIN/`pg_trgm` (§1.2), não por falta de cache. Índice deixa rápido **sem** criar obrigação de coerência; cache deixa rápido e cria uma, para sempre |

**Se um dia precisar mesmo:** o alvo é o `COUNT` do total de páginas, não as páginas. Ele é
a metade cara, muda só quando um livro é criado ou desativado, e tem superfície de
invalidação minúscula. Ou abandona-se o total exato e devolve-se `hasNext`, que é de graça.

`CacheKeys.BookCatalogPage` foi **removida** do boilerplate: ela existia pressupondo esta
decisão em sentido contrário, e ainda por cima não incluía `q` na chave — duas buscas
diferentes na mesma página colidiriam e uma serviria o resultado da outra.

### Alternativas

| Alternativa | Ganho | Por que não |
| --- | --- | --- |
| **Atualizar** o cache com o novo valor em vez de invalidar | Evita o *miss* seguinte; latência menor no pico | Duas escritas concorrentes podem chegar ao Redis fora de ordem e deixar o valor **errado** ali. `DEL` converge para a verdade (a próxima leitura repopula da fonte); `SET` pode convergir para uma mentira plausível que ninguém questiona até o TTL. Num objeto composto é pior ainda: exigiria ler-modificar-gravar sem lock, recriando no cache o *lost update* que combatemos no banco |
| Duas chaves — descritivo com TTL longo, disponibilidade com TTL curto | Um empréstimo não descartaria título e autor, que não mudaram | Separar chave só compensa quando as partes têm **custo de carga** ou **origem** diferentes. Aqui as duas metades moram na mesma linha: qualquer *miss* lê a linha inteira, então nada se economiza. Dobraria o código de invalidação pelo mesmo resultado |
| `SET`/`DECR` atômico num contador puro | `DECR` é comutativo, então a ordem deixaria de importar | Não é atômico com o commit — morrer entre `COMMIT` e `DECR` deixa o contador permanentemente errado. E `DECR` em chave ausente cria a chave com `−1`: disponibilidade negativa publicada |
| Write-through / write-behind | Cache sempre populado e coerente | Põem o cache no caminho de **escrita**. No write-behind ele chega a ser a fonte de verdade por um intervalo — o enunciado proíbe explicitamente |
| `HybridCache` (.NET 9+): L1 em memória + L2 Redis | Proteção contra *stampede* embutida, latência de L1 | O L1 é **local a cada réplica** e não é invalidado pelas outras: com 2 a 11 pods, cada um serviria uma disponibilidade diferente por até o TTL local. É exatamente o que não pode acontecer neste desafio |
| `IConnectionMultiplexer` direto, sem `IDistributedCache` | Acesso a pipelines, Lua e pub/sub | A abstração cobre o que preciso hoje. Voltaria a ela para invalidação por padrão de chave ou para fan-out via pub/sub |
| Cachear a listagem `GET /books` | É de fato a consulta **mais cara** — filtro, `ORDER BY`, `OFFSET` e um `COUNT` | Não é dificuldade técnica: um **contador de geração** na chave (`books:v{N}:page:...` mais `INCR` a cada escrita) invalidaria N páginas em O(1), sem varrer keyspace. O problema é a **taxa de acerto**: enquanto a listagem trouxer disponibilidade, todo empréstimo a invalida inteira, e o cache é descartado antes de ser reaproveitado. E o gargalo real da busca `q` se resolve com **índice** (GIN/`pg_trgm`), não com cache — índice deixa rápido sem criar obrigação de coerência. Se ainda doer, o alvo é o `COUNT`, não as páginas |
| Não usar cache | Uma peça a menos, zero risco de divergência | O desafio exige Redis em pelo menos uma leitura |

**Custo assumido — a janela de corrida clássica:** um leitor que carregou o valor antigo
do banco pode gravá-lo no cache **depois** da invalidação, deixando a entrada obsoleta até
o TTL expirar. Mitigação: TTL curto. **Não é problema de corretude** porque o PostgreSQL
continua sendo a autoridade — o cache nunca participa da decisão de emprestar, só de
leituras informativas. Vai explícito no README.

**Segundo custo:** *cache stampede*. Depois de uma invalidação, N leitores simultâneos vão
todos ao banco. Aceito para o volume do desafio; a solução (single-flight/lock por chave)
não se paga aqui.

**Terceiro custo — o TTL é rede de segurança, não só política de frescor.** Se o processo
morrer entre o `COMMIT` e o `DEL`, ou se o Redis estiver fora (o `RedisCacheStore` registra
log e engole a falha), a invalidação se perde e nada no sistema conserta isso — exceto o
TTL expirando. Os 60 s são, na prática, **o tempo máximo de divergência depois de uma
invalidação perdida**. É esse o motivo de o TTL ser curto, mais do que a volatilidade do
dado.

**Quarto custo:** o snapshot cacheado vira um contrato informal. Acrescentar um campo à
resposta do livro faz as entradas já gravadas voltarem sem ele por até 60 s após o deploy.
O TTL curto resolve sozinho; se algum campo for crítico, versiona-se o prefixo da chave
(`bookrent:v2:book:{id}`) no deploy que muda o formato.

**Magnitude honesta do ganho:** `GET /books/{id}` é um lookup por chave primária, servido
quase sempre de *shared buffers*. O cache aqui não compra latência — compra **alívio de
conexão do pool**, que é o recurso escasso desta arquitetura (11 réplicas × tamanho do
pool contra o `max_connections` do PostgreSQL). A decisão de cache com impacto grande seria
a listagem, e ela fica de fora pelos motivos acima.

**Mudaria se:** (a) adotássemos `BookCopy` (§1.1) — disponibilidade viraria `COUNT` sobre
outra tabela, com origem e custo diferentes do descritivo, e aí **separar em duas chaves
passaria a ser a escolha certa**; ou (b) a representação do livro ganhasse *joins*
(categorias, editora, autores em tabela própria), tornando o descritivo caro de remontar e
justificando mantê-lo cacheado através dos empréstimos.

---

## 6. Contrato dos endpoints

Erros em **Problem Details** (`application/problem+json`) com `code` (código estável da
regra) e `correlationId` nas extensions — já implementado no `DomainExceptionHandler`,
que hoje devolve sempre 409 e será estendido para mapear famílias de código.

### 6.1 Escolha dos status HTTP

> **Escolha:** 400 para sintaxe/cabeçalho ausente · 404 para recurso inexistente · **409
> para conflito de estado** (sem exemplar, empréstimo já devolvido, ISBN duplicado) ·
> **422 para semântica inválida do payload** (chave reusada com corpo diferente, total
> abaixo dos empréstimos ativos).

| Alternativa | Ganho | Por que não |
| --- | --- | --- |
| 400 para toda regra de negócio | Mais simples, menos superfície | O cliente não distingue "seu pedido está malformado, não repita" de "o estado mudou, pode fazer sentido tentar de novo" — distinção que importa para retry automático |
| 422 para tudo que não é sintaxe | Consistente | Perde a semântica de *conflito*, que é justamente o caso central do desafio (o último exemplar) |
| 409 também para o replay idempotente | Sinaliza a repetição no status | O desafio pede "a resposta previamente produzida"; devolver conflito contradiz o propósito da idempotência |

### 6.2 Catálogo

| Método   | Rota 										  | Sucesso 		 | Erros |
| ---      | --- 		   								  | --- 			 | --- |
| `POST`   | `/books`      								  | 201 + `Location` | 422 validação · 409 `book.isbn_already_exists` |
| `GET`    | `/books?page=&pageSize=&q=&includeInactive=` | 200 			 | —   												|
| `GET`    | `/books/{id}` 								  | 200 			 | 404 												|
| `PATCH`  | `/books/{id}` 								  | 200 			 | 404 · 409 ISBN duplicado / conflito de concorrência · 422 `book.total_below_active_loans` |
| `DELETE` | `/books/{id}` 								  | 204 			 | 404 · 409 `book.has_active_loans` |

> **Escolha:** `DELETE` **nunca apaga** — desativa (`IsActive = false`) e audita. Com
> empréstimos **ativos**, recusa com 409. Histórico encerrado não impede a desativação e
> continua intacto depois dela.

| Alternativa | Ganho | Por que não |
| --- | --- | --- |
| `POST /books/{id}/deactivate` e `DELETE` → 405 | Semanticamente mais honesto: um `DELETE` que não deleta surpreende quem lê o contrato | O desafio sugere `DELETE /books/{id}` e diz *"desativar **ou** rejeitar a remoção"* — desativar é a opção explicitamente oferecida. Fico com o contrato sugerido e documento |
| Apagar de fato quando não há histórico | "Delete" faz o que promete no caso simples | Dois comportamentos para o mesmo verbo, dependendo de estado invisível ao cliente. Pior de prever do que uma regra única |

**Custo assumido:** o *soft delete* contamina toda consulta com o filtro `IsActive`.
Esquecer o filtro num lugar expõe livro desativado.

**Como ficou, e a diferença em relação ao que este plano previa:** a mitigação prevista era
um *global query filter* do EF Core com `IgnoreQueryFilters()` onde o histórico precisasse
enxergar inativos. Não foi implementada — o filtro é um `Where(b => b.IsActive)` explícito,
em um único ponto (`BookRepository.SearchAsync`), porque é a **única** consulta que deve
escondê-los: leitura por id, disponibilidade e histórico mostram livro desativado de
propósito. Um filtro global obrigaria `IgnoreQueryFilters()` em quase todo lugar,
invertendo o padrão e o risco: em vez de esquecer de esconder, esqueceria-se de revelar,
e o histórico sumiria em silêncio — o defeito mais grave dos dois. **Custo real assumido:**
se surgir uma segunda consulta que deva esconder inativos, o filtro terá de ser repetido à
mão.

`PATCH` reduzindo `TotalCopies` abaixo dos empréstimos ativos → 422. O ajuste move
`AvailableCopies` pelo mesmo delta, no mesmo commit.

### 6.3 Usuários e empréstimos

| Método | Rota 									   | Sucesso | Erros |
| --- 	 | ---  									   | --- | --- |
| `POST` | `/users` 								   | 201 + `Location` | 422 · 409 `user.email_already_exists` |
| `GET`  | `/users/{id}/loans?status=&page=&pageSize=` | 200 | 404 |
| `POST` | `/loans` *(exige `Idempotency-Key`)* 	   | 201 (ou 201 replay) | 400 sem header · 404 livro/usuário · 409 `loan.no_copies_available` · 409 `book.inactive` · 422 chave reusada |
| `POST` | `/loans/{id}/return` 					   | 200 | 404 · 409 `loan.not_active` |
| `POST` | `/loans/{id}/cancel` 					   | 200 | 404 · 409 `loan.not_active` |
| `GET`  | `/books/{id}/availability` 				   | 200 *(cacheado)* | 404 |
| `GET`  | `/books/{id}/history?page=&pageSize=` 	   | 200 | 404 |
| `GET`  | `/audit-events?...` 						   | 200 | — |

**Ajustes ao contrato sugerido, todos a documentar no README:** `GET /books` ganha
paginação e busca; entra `GET /users/{id}`; o replay idempotente devolve **201** (a
resposta originalmente produzida) com `Idempotency-Replayed: true`, em vez de 200.

### 6.4 Paginação por offset

> **Escolha:** `page`/`pageSize` com `OFFSET`/`LIMIT`, `pageSize` limitado a 100.

| Alternativa | Ganho | Por que não |
| --- | --- | --- |
| Cursor (keyset) | Custo constante em qualquer página; imune a deslocamento quando linhas são inseridas durante a navegação | Contrato mais complexo (token opaco), sem "ir para a página N" e sem total. Desproporcional ao volume do desafio |

**Custo assumido:** `OFFSET` grande degrada linearmente, e uma inserção concorrente pode
fazer um item aparecer duas vezes entre páginas. Vai como limitação conhecida.

### 6.5 Conflito de edição no `PATCH`: tradução e política de retry

> **Escolha:** conflito de edição responde **409** `book.concurrent_modification`,
> **sem retry automático** — volta para o cliente decidir. Como o conflito é detectado
> muda conforme o caminho, e a razão está abaixo.

**Correção feita durante a implementação.** Este plano previa detectar o conflito do
`PATCH` por `DbUpdateConcurrencyException` do EF Core. Isso **não funciona** aqui, e o
motivo é o mesmo que justificou a §9.7: o change tracker só sabe escrever valores
**absolutos**, e `available_copies` precisa de escrita **relativa**. Um empréstimo
concorrente muda a disponibilidade sem tocar em `version` (por desenho), então um
`SaveChanges` gravaria `available_copies = <lido> + delta` e **perderia esse empréstimo** —
exatamente o *lost update* que a §2.4 descreve, reaparecendo pelo outro lado.

Como ficou, e por quê:

| Caminho | Escreve | Detecção do conflito |
| --- | --- | --- |
| `PATCH /books/{id}` | contador (relativo) + descritivos | `UPDATE` condicional com `version` no `WHERE`; **contagem de linhas**, não exceção |
| `DELETE /books/{id}` | só descritivos (`is_active`) | change tracker + token → `DbUpdateConcurrencyException` |

É a regra "absoluto vs. relativo" da §2.4 aplicada de forma consistente: o `DELETE` não
encosta no contador, então a escrita absoluta é segura e o mecanismo do EF Core basta.

O `PersistenceExceptionHandler` existe e cobre o caminho do `DELETE`, mais violação de
índice único (`23505`) — a checagem prévia de ISBN é amigável, mas quem garante é o
índice, e duas criações simultâneas com o mesmo ISBN passam pela checagem e colidem nele.

**Por que não fazer retry automático:** repetir sozinho significaria **mesclar duas edições
humanas**, e mesclagem automática perde intenção. Se um bibliotecário corrigiu o autor e
outro corrigiu o título, gravar os dois por cima do último estado pode produzir um
registro que nenhum dos dois pediu. A resposta certa é "alguém alterou este livro enquanto
você editava, veja como está agora" — decisão de quem edita, não do servidor.

| Alternativa | Ganho | Por que não |
| --- | --- | --- |
| Retry automático relendo e reaplicando | Cliente nunca vê o conflito | Perde intenção: sobrescreve silenciosamente a edição de outra pessoa. É *last write wins* com passos extras |
| Mesclagem campo a campo no servidor | Resolve o caso em que os dois editaram campos diferentes | O servidor não sabe se duas edições são compatíveis. Regra automática que erra em silêncio é pior que um 409 explícito |
| 412 `Precondition Failed` com `If-Match`/`ETag` | Semântica HTTP canônica para edição condicional | Exige expor a versão como `ETag` e o cliente enviar `If-Match`. Mais correto no papel, mais contrato para o escopo do desafio. Fica registrado como evolução natural |

**Custo assumido:** o cliente precisa saber lidar com 409 no `PATCH` — recarregar e
reenviar. Numa UI é o comportamento desejado; num script, é trabalho a mais.

**Distinguir os dois motivos de falha do `PATCH` de quantidade.** O `UPDATE` do `PATCH`
carrega duas condições: a do token (`version = @esperada`) e a da invariante
(`available_copies + @delta >= 0`). Zero linhas afetadas não diz qual das duas falhou —
é preciso reler a linha para classificar entre **409** (versão mudou) e **422**
`book.total_below_active_loans` (reduziria o acervo abaixo dos empréstimos ativos). Uma
leitura extra apenas no caminho de erro.

---

## 7. Testes

### 7.1 Escolhas de ferramenta

| Decisão | Alternativa | Por que a escolha |
| --- | --- | --- |
| **Testcontainers** (PostgreSQL e Redis reais) | Banco in-memory / SQLite | O desafio exige integração com PostgreSQL real, e os cenários de concorrência dependem do comportamento **específico** do PostgreSQL (`EvalPlanQual`, locks). In-memory testaria uma semântica que não existe em produção — o pior tipo de teste verde |
| | PostgreSQL compartilhado no CI | Mais rápido, mas estado compartilhado entre execuções e dependência de infraestrutura externa para rodar `dotnet test` na máquina de qualquer um |
| **Shouldly** | FluentAssertions | FluentAssertions passou a exigir licença comercial na v8; Shouldly é Apache-2.0. Decisão de licenciamento, não de gosto |
| ~~**NSubstitute**~~ | Moq | **Dependência removida.** A escolha se justificaria (sintaxe mais limpa, sem o episódio do SponsorLink), mas nenhum teste chegou a usar dublê: o domínio recebe o instante como parâmetro e os testes de integração usam PostgreSQL e Redis reais. Defender uma decisão sobre um pacote não utilizado seria pior que não tê-la |
| **xUnit v3** | NUnit / MSTest | Suporte nativo a `CancellationToken` por teste (`TestContext.Current`) e ao modelo de projeto executável; já é o padrão do repositório |

### 7.2 Compartilhar containers entre classes

> **Escolha:** um par de containers para toda a coleção de testes (`ICollectionFixture`),
> como já está no `IntegrationTestSuite`.

**Custo assumido, e é o mais importante desta seção:** os testes **não podem assumir banco
vazio**. Cada teste precisa criar seus próprios dados com identificadores únicos e afirmar
apenas sobre eles. Um teste que fizesse `SELECT COUNT(*) FROM loans` seria frágil por
construção. A alternativa (um par de containers por classe) daria isolamento perfeito ao
custo de dezenas de segundos por classe.

**Mudaria se:** a suíte crescesse a ponto de a interferência entre classes virar fonte de
teste intermitente — aí, respawn do schema entre classes.

### 7.3 Unitários (`BookRent.UnitTests`, sem I/O)

- `Book`: ISBN normalizado e validado; título/autor obrigatórios; `TotalCopies >= 0`;
  desativar duas vezes é erro; ajuste de quantidade move a disponibilidade pelo delta.
- `Loan`: máquina de estados — devolver ativo ✓, devolver devolvido ✗, cancelar devolvido
  ✗, cancelar ativo ✓; `DueAt = LoanedAt + 14 dias` com `TimeProvider` falso.
- `User`: e-mail normalizado e validado.
- Arquitetura: os dois testes de dependência entre camadas, já existentes.

### 7.4 Integração — os quatro cenários exigidos, mais um

1. **Último exemplar sob concorrência** — livro com 1 exemplar, N requisições `POST /loans`
   disparadas juntas (chaves distintas, barreira de largada comum). Asserções: exatamente
   1 × 201; N−1 × 409 com `code = loan.no_copies_available`; `AvailableCopies = 0`;
   exatamente 1 linha em `loans` para aquele livro.
2. **Idempotência** — mesma chave duas vezes → um único empréstimo, disponibilidade
   decrementada uma vez, segunda resposta idêntica com `Idempotency-Replayed`. Variantes:
   mesma chave com corpo diferente → 422; duas requisições **simultâneas** com a mesma
   chave → um empréstimo só.
3. **Preservação do histórico** — empresta, devolve, cancela outro; `history` e
   `users/{id}/loans` seguem mostrando os empréstimos com o status final; `DELETE` desativa
   sem apagar; `audit-events` traz os eventos.
4. **Cache coerente** — lê `availability` (popula), cria empréstimo, relê e o valor
   reflete o decremento; e a chave é populada na leitura e removida na escrita, verificado
   direto no Redis — sem isso a suíte passaria até com o cache morto, já que
   `RedisCacheStore` engole falhas por desenho.

   > **Não implementado:** o teste que derruba o container do Redis para provar a
   > degradação. A resiliência está no código (`RedisCacheStore` captura tudo que não seja
   > cancelamento e cai para o banco) e é exercitada de fato — os testes de integração de
   > catálogo e empréstimo rodam contra Redis real —, mas nenhum teste **remove** a
   > dependência. É a lacuna conhecida da suíte.
5. **Conflito otimista no `PATCH`** *(além do mínimo exigido)* — dois `PATCH` concorrentes
   no mesmo livro, ambos partindo da mesma versão: exatamente **1 × 200** e **1 × 409**
   com `code = book.concurrent_modification`, e o estado final igual ao da edição que
   venceu — nenhuma mesclagem silenciosa.

   Complemento que prova a ausência de conflito falso da §2.4(b): um `PATCH` de título
   concorrente com um `POST /loans` do mesmo livro deve resultar em **ambos bem-sucedidos**
   — o empréstimo mexe no contador, a edição mexe nos campos descritivos, e o token não
   os confunde.

   Este cenário não está na lista mínima do desafio. Entra porque a suíte passa a
   demonstrar **as duas estratégias de concorrência de propósito** (§2.4): o cenário 1
   exercita o caminho pessimista/atômico, este exercita o otimista.

> **Custo assumido — honestidade sobre o teste de corrida:** um teste de concorrência é
> probabilístico. Passar uma vez **não prova** ausência de condição de corrida; só mostra
> que naquela execução não houve. Mitigo repetindo o cenário algumas dezenas de vezes e
> afirmando sobre o **estado final do banco**, nunca sobre ordem ou tempo. A garantia real
> vem do `UPDATE` condicional e da `CHECK` constraint — o teste é evidência, não prova.

---

## 8. Ordem de execução

Um commit por etapa, com a suíte verde ao fim de cada uma.

**Concluídas:** 1 a 6. As etapas 5 e 6 saíram num commit só — os testes que provam a
5 (último exemplar, idempotência) afirmam sobre o estado final via
`GET /books/{id}/availability`, que é entrega da 6; separá-las exigiria commitar código
sem verificação do resultado.

**Concluídas: 1 a 8.** Depois delas, um code review independente encontrou seis defeitos
— um de severidade alta (retry transitório duplicando registros) — todos corrigidos com
teste de regressão. Ver o histórico do Git a partir de `73efe8d`.

| # | Etapa | Entrega |
| --- | --- | --- |
| 1 | **Domínio** | `Book` (com `Version` e o ajuste de quantidade por delta), `User`, `Loan` com a máquina de estados, `AuditEvent`, `IdempotencyRecord`, `LoanStatus`, catálogo de códigos de erro. Ids `Guid.CreateVersion7()` (§9.6). Testes unitários das invariantes (§7.3) |
| 2 | **Persistência** | `IEntityTypeConfiguration` por entidade, `DbSet`s, `CHECK (available_copies >= 0 AND <= total_copies)`, `Version` como `IsConcurrencyToken()`, `AuditEvent.Id` como `bigint identity`, índices da §1.2 **incluindo a extensão `pg_trgm` e os índices GIN**, `AuditEvent` bloqueado para `UPDATE`/`DELETE` no `DbContext`, **migration inicial** |
| 3 | **Catálogo** | Casos de uso e endpoints `/books` (POST, GET lista, GET id, PATCH, DELETE). `PATCH` com token otimista; **`IExceptionHandler` traduzindo `DbUpdateConcurrencyException` em 409 `book.concurrent_modification`** (§6.5), com releitura para separar 409 de 422. Auditoria de `book.created`/`updated`/`deactivated`. Cache-aside na chave única (§5) |
| 4 | **Usuários** | `POST /users`, `GET /users/{id}`, `GET /users/{id}/loans` |
| 5 | **Empréstimo** | `POST /loans`: `UPDATE` condicional atômico (§2.1), idempotência pelo índice único (§3), métricas de `LoanMetrics`, auditoria de `loan.created`, invalidação de cache **após o commit** |
| 6 | **Ciclo de vida** | `return`, `cancel`, `GET /books/{id}/history`, `GET /books/{id}/availability`, `GET /audit-events` com filtros e paginação |
| 7 | **Testes de integração** | Os **cinco** cenários da §7.4, substituindo os três `Skip`: último exemplar, idempotência (3 variantes), preservação de histórico, coerência de cache, e **conflito otimista no `PATCH` + ausência de conflito falso** |
| 8 | **Fechamento** | README com as seções que o desafio exige (concorrência, idempotência, cache, auditoria, 2–11 réplicas, limitações), revisão de status/erros ponta a ponta, `dotnet test BookRent.slnx` verde |

---

## 9. Decisões já tomadas no boilerplate

Estão no repositório e serão questionadas do mesmo jeito. Registro aqui com o mesmo
formato.

### 9.1 Monólito modular, não microserviços

> **Escolha:** um processo, um banco, escalado por réplicas.

**Alternativa:** catálogo e empréstimos como serviços separados. **Por que não:** o
desafio exige "exatamente um empréstimo bem-sucedido" sob concorrência — isso se resolve
com uma transação local. Separar forçaria saga ou outbox e introduziria consistência
eventual **exatamente onde ela é inaceitável**. **Custo:** uma unidade de deploy só;
escala por réplica, não por serviço; o banco é o ponto de contenção comum. **Mudaria se:**
catálogo e empréstimos tivessem perfis de carga ou donos muito diferentes.

### 9.2 Quatro projetos (Clean Architecture) para um domínio pequeno

**Alternativa:** projeto único com pastas / *vertical slices* — menos cerimônia, menos
arquivos, mais rápido de navegar. **Por que a escolha:** a fronteira entre domínio e
Npgsql é o que permite testar regra de negócio sem I/O, e a separação em projetos torna a
regra **verificável** — `LayerDependencyTests` quebra o build se alguém referenciar
infraestrutura no domínio. Em pastas, isso seria convenção não verificada. **Custo:** mais
indireção e mais arquivos do que o tamanho do domínio pede; o próprio desafio avisa que o
padrão não é avaliado isoladamente. **Mudaria se:** o escopo fosse permanentemente deste
tamanho e a equipe valorizasse navegação sobre fronteira.

### 9.3 Minimal APIs, não Controllers

**Alternativa:** Controllers — filtros, model binding mais rico, familiaridade. **Custo da
escolha:** validação e *cross-cutting* precisam ser montados com endpoint filters. **Por
quê:** menos cerimônia, agrupamento explícito por `MapGroup` e integração direta com o
OpenAPI nativo do .NET 10.

### 9.4 Sem MediatR/CQRS

**Alternativa:** MediatR com *pipeline behaviors* para validação, log e transação — evita
repetir essas chamadas em cada caso de uso. **Por que não:** uma dependência e uma camada
de indireção para ~10 casos de uso; o ganho do pipeline aparece com dezenas. **Custo:**
validação e transação são chamadas explicitamente em cada caso de uso — repetição
controlada, e o rastro de execução fica direto no código em vez de num despachante.
**Mudaria se:** passasse de ~20 casos de uso ou surgisse necessidade de *cross-cutting*
uniforme e obrigatório.

### 9.5 EF Core, com escape para SQL no caminho quente

O desafio obriga EF Core + Npgsql. **Decisão relevante:** o decremento de disponibilidade
usa `ExecuteUpdateAsync` (SQL direto, sem *change tracker*), justamente para **não** cair
no ciclo ler-modificar-gravar que o *change tracker* induz e que abriria a janela de
corrida. **Custo:** essa escrita não passa pelo modelo em memória (ver §2.2).

### 9.6 Chave primária: `Guid` (UUIDv7) no que é exposto, `bigint` na auditoria

> **Escolha:** `Guid` gerado com `Guid.CreateVersion7()` (.NET 9+) em `Book`, `User` e
> `Loan` — as entidades cujo id aparece na URL. `AuditEvent` usa `bigint identity`.
> `IdempotencyRecord` não tem chave substituta: a PK é composta pela chave natural
> `(Endpoint, Key)` — ver §1.2.

**O motivo é segurança de exposição, não concorrência.** Vale desfazer uma confusão comum
antes de tudo: **`bigint identity` não tem problema algum com múltiplas réplicas.** No
PostgreSQL ele usa uma *sequence*, e `nextval()` é atômico entre sessões, conexões e
processos — inclusive opera fora da transação, justamente para não serializar inserções
concorrentes. Duas ou onze réplicas dão no mesmo. O que quebraria uma sequence seriam
**múltiplos bancos** escrevendo (multi-master, sharding, merge de bases), e não é o caso:
um PostgreSQL, N clientes.

| Alternativa | Ganho | Por que não |
| --- | --- | --- |
| `bigint identity` em tudo | 8 bytes em vez de 16, na PK e em toda FK e índice; ordenação natural | Id sequencial em rota pública é previsível: `GET /loans/1042` revela que existem ~1042 empréstimos e que `/1041` provavelmente existe. **Esta API não tem autenticação** — id sequencial vira convite a varredura |
| `Guid` v4 (aleatório) | 122 bits aleatórios, ainda menos colidível que o v7; não vaza o instante de criação | Cada inserção cai numa página aleatória do índice B-tree: fragmentação e perda de localidade de cache. O v7 insere no fim do índice, como um `bigint` |
| `Guid` v7 **também** em `AuditEvent` | Uniformidade de modelo | É a tabela que mais cresce, o id dela nunca é FK em lugar nenhum e nunca vai numa URL. Pagar 16 bytes por linha ali é custo sem contrapartida |

**Por que UUIDv7 e não v4:** os 48 bits iniciais são o timestamp em milissegundos, então
os valores crescem com o tempo e as inserções vão para o fim do índice. Detalhe que
importa: o `Guid` do .NET tem layout *mixed-endian* na memória, mas o Npgsql converte para
a ordem canônica RFC ao gravar no tipo `uuid` — o valor no banco é um UUIDv7 legítimo e
`ORDER BY id` devolve ordem de criação de verdade. Sem essa conversão, o v7 perderia
exatamente o benefício que justifica escolhê-lo (foi o problema que levou o SQL Server a
criar o `NEWSEQUENTIALID`; no Npgsql não existe).

**Sobre colisão.** UUIDv7 tem 74 bits aleatórios, e uma colisão exige dois valores gerados
**no mesmo milissegundo** com os mesmos 74 bits. A 1 milhão de inserções por segundo
sustentadas por um ano, a chance de *alguma* colisão é de ~1 em 1,2 milhão — esta API fará
centenas por segundo. Mas o argumento que fecha a questão não é probabilístico: **se
colidisse, a PK rejeitaria o `INSERT` com `23505 unique_violation`** — uma requisição que
falha de forma visível, não um dado corrompido em silêncio. É a mesma filosofia da
`CHECK (available_copies >= 0)` da §2.1: a garantia mora numa restrição do banco, não na
probabilidade nem na correção do código.

**Custos assumidos:**
- 16 bytes por chave em `Book`, `User` e `Loan`, propagados a toda FK e a todo índice que
  as inclua — o dobro de um `bigint`.
- O v7 **vaza o instante de criação** nos 48 bits de timestamp: quem vê o id de um
  empréstimo sabe o milissegundo em que foi criado. Inofensivo neste domínio; em outro
  (identificador de usuário, por exemplo) poderia não ser.
- Duas estratégias de chave no mesmo modelo, em vez de uma regra só.

**Mudaria se:** houvesse autenticação e autorização por recurso — aí a previsibilidade do
id deixaria de ser exposição, e `bigint` em tudo passaria a ser a escolha mais barata.

### 9.7 Token de concorrência do `PATCH`: coluna `Version` gerenciada pela aplicação

> **Escolha:** coluna `Version int` em `Book`, marcada com `IsConcurrencyToken()`,
> **incrementada apenas pelo `PATCH`/`DELETE`** — as operações que escrevem campos
> descritivos. Empréstimo, devolução e cancelamento escrevem `available_copies` e **não**
> tocam em `Version`.

**Por que não `xmin`** (que era a escolha anterior deste plano): `xmin` é coluna de sistema
do PostgreSQL e muda a **cada** `UPDATE` na linha, inclusive os que só mexem no contador.
Consequência prática:

```
t0   { title='Dom Casmuro', available=5, xmin=100 }
t1   Bibliotecário abre a edição                  → lê xmin=100
t2   Usuário empresta → SET available_copies = 4  → xmin vira 101
t3   Bibliotecário salva o título
     UPDATE ... WHERE xmin=100                    → 0 linhas → 409
```

O bibliotecário recebe "alguém alterou este livro" sem que ninguém tenha tocado no título
— e num livro movimentado isso se repete indefinidamente: ele nunca consegue salvar. É o
**conflito falso** da §2.4(b), atingindo o `PATCH` em vez do empréstimo. Com `Version`, o
`t2` não altera o token e o `t3` grava normalmente: as duas operações vencem, porque nunca
estiveram em conflito real.

**O que protege o contador, já que o token não o cobre:** nada precisa — `available_copies`
nunca é escrito de forma absoluta (§2.4). Mesmo dentro do `PATCH` que muda a quantidade, a
escrita é relativa e condicional:

```sql
UPDATE bookrent.books
   SET total_copies = @novoTotal,
       available_copies = available_copies + @delta,   -- relativo, não "= @valorLido"
       version = version + 1
 WHERE id = @id
   AND version = @versaoLida
   AND available_copies + @delta >= 0;
```

A condição é avaliada pelo banco contra o valor **corrente**, não contra o que foi lido na
abertura da tela — por isso ela sozinha impede reduzir o acervo abaixo dos empréstimos
ativos, sem ajuda do token. Gravar `available_copies = @valorLido + @delta` (absoluto)
quebraria a invariante `total − available = ativos` diante de um empréstimo concorrente;
é exatamente o erro que a escrita relativa torna impossível.

| Alternativa | Ganho | Por que não |
| --- | --- | --- |
| `xmin` | Coluna de sistema: sem coluna extra, sem trigger, sem disciplina de incremento | Granularidade de **linha**: todo empréstimo invalida toda edição em andamento |
| Campos descritivos como tokens individuais (`IsConcurrencyToken()` em `Title`, `Author`, `Isbn`, `TotalCopies`) | Sem coluna para manter e sem risco de esquecer o incremento | `WHERE` grande, valores originais trafegando em todo `UPDATE`, comparação de strings longas; e o conflito passa a ser por campo, não por "estado do livro" |
| Mover `available_copies` para tabela própria | A linha do livro fica estável e o `xmin` voltaria a servir | Uma tabela e um `join` a mais em quase toda leitura, para resolver o que uma coluna resolve |
| `rowversion` | — | Recurso do SQL Server; o desafio veta explicitamente |

**`int` e não `Guid`** (o exemplo da documentação do EF Core usa `Guid`): legível num dump,
ordenável, e trivial de expor como `ETag` se formos para `If-Match` — a evolução registrada
na §6.5.

**Custo assumido — e é disciplina, não código:** token gerenciado pela aplicação depende de
que ninguém crie um caminho que carregue `Book` no *change tracker* para mexer em
disponibilidade. Se isso acontecer, o token entra em cena onde não deveria e o conflito
falso volta. Duas defesas: a regra da §2.4 (disponibilidade só via `ExecuteUpdateAsync`,
que escreve exatamente as colunas nomeadas) e o teste complementar do cenário 5 da §7.4,
que exige que um `PATCH` de título concorrente com um `POST /loans` termine com **ambos**
bem-sucedidos. Quebrou a regra, o teste cai.

**Ganho colateral:** ao contrário do `xmin`, a coluna é portável — trocar de banco não
exigiria repensar a estratégia.

**Mudaria se:** o número de campos descritivos ficasse pequeno e estável a ponto de marcar
cada um como token individual sair mais barato que manter a coluna.

### 9.8 `DateTimeOffset` + `timestamptz`

**Alternativa:** `DateTime` com `Kind = Utc`. **Por que:** `DateTimeOffset` carrega a
intenção no tipo, sem depender de disciplina para não vazar horário local. **Custo:** o
offset será sempre zero — informação redundante, já que o `timestamptz` normaliza para UTC
de qualquer forma.

### 9.9 Migrations por `Job`, não no startup

Já documentado no README e no `migration-job.yaml`. **Alternativa:** migrar no startup,
como o Compose faz. **Por que não em produção:** N réplicas disputando o lock de migration
e schema acoplado ao ciclo de vida do pod. **Custo:** mais uma etapa no pipeline e uma
imagem de bundle a construir — hoje uma pendência conhecida.

### 9.10 `TreatWarningsAsErrors` ligado

**Custo:** atrito real — um *warning* de analisador para o build. **Por quê:** avisos
ignorados acumulam até ninguém mais ler a saída do build. As exceções ficam no
`.editorconfig`, cada uma com justificativa ao lado.

### 9.11 Health `live` separado de `ready`

Já documentado. **Por quê:** se o *liveness* checasse dependências, uma queda do banco
reiniciaria pods saudáveis em cascata, transformando indisponibilidade parcial em total.

### 9.12 Entre 2 e 11 réplicas: a corretude é indiferente, a capacidade não

Duas perguntas diferentes que costumam ser tratadas como uma só.

**Corretude — indiferente ao número de réplicas.** O banco é um só, e **o lock mora nele,
não na aplicação**. Duas requisições disputando o último exemplar podem cair no mesmo pod,
em pods diferentes ou em zonas diferentes: as duas viram transações no mesmo PostgreSQL,
competindo pelo mesmo *row lock*. Subir de 2 para 11 não cria um problema novo — adiciona
clientes a um gerenciador de locks que já resolve N clientes por construção. A aplicação
ser *stateless* não é estilo: é a condição que permite ao banco ser o único ponto de
serialização.

O que **quebraria** essa premissa:

| Cenário | Quebra? | Por quê |
| --- | --- | --- |
| Mais réplicas da API | Não | Mais clientes, mesmo lock |
| Réplicas de **leitura** do PostgreSQL | Não, no caminho crítico | O desenho nunca "lê e depois decide" — a disponibilidade é decidida pelo próprio `UPDATE`, no primário. Um `GET` servido por réplica traria número defasado, mesma classe do cache e aceitável pelo mesmo motivo |
| *Failover* para um standby | Não | Transações em voo sofrem rollback e o cliente recebe erro: falha, não inconsistência. Como empréstimo, auditoria e idempotência estão no mesmo commit, não sobra meia operação — e o retry reencontra a chave, ou não encontra nada porque o rollback a liberou |
| **Multi-primário / active-active** | **Sim** | Dois primários decrementando cada um a sua cópia da linha e reconciliando depois: a invariante morre. É o único cenário que realmente derruba a estratégia |
| Sharding por livro | Não | A linha continua existindo em exatamente um shard, e o empréstimo toca um shard só |
| PgBouncer em modo *transaction* | Não | A transação fica amarrada a uma conexão de servidor pelo tempo dela; os locks funcionam normalmente |

**Capacidade — aí o número de réplicas importa muito.** O pool de conexões do Npgsql é
**por processo**, e o `max_connections` do PostgreSQL é do servidor inteiro:

```
maxReplicas (hpa.yaml) x Maximum Pool Size  =  teto de conexoes fisicas
PostgreSQL max_connections padrao = 100, menos as reservadas ~= 97 uteis
```

Com o `Maximum Pool Size=40` que estava configurado, `11 x 40 = 440` — mais de quatro vezes
o que o servidor aceita. **Ajustado para 8** (`11 x 8 = 88`), deixando folga para o `Job` de
migrations, conexões administrativas e monitoramento.

As duas falhas se comportam de forma diferente, e a distinção importa: estourar o teto do
**pool** faz a requisição *esperar* (até o `Timeout`, padrão 15 s); estourar o
`max_connections` do **servidor** faz o PostgreSQL *recusar* com `53300
too_many_connections`. Degradação num caso, indisponibilidade no outro.

**Por que isso é pior justamente sob contenção.** Conexão simultânea é função de
`taxa x tempo de posse`, e dois pontos do desenho seguram conexão *enquanto esperam*: o
`INSERT` de idempotência bloqueado no índice único (§3) e os empréstimos enfileirados no
*row lock* do livro (§2.1). Ou seja, **contenção vira consumo de conexão** — e o cenário que
o desafio mais estressa é exatamente o que infla o pool.

**Onde o valor precisa estar configurado:** no Kubernetes a connection string vem do
`Secret` e **sobrescreve a do `appsettings.json` por inteiro**. Configurar o pool só no
`appsettings.json` não teria efeito em produção; por isso o limite aparece no exemplo de
`deploy/k8s/secret.example.yaml`, com a conta ao lado.

| Alternativa | Ganho | Por que não (agora) |
| --- | --- | --- |
| **PgBouncer** em modo *transaction* | A resposta de produção: pods mantêm pools grandes e ele multiplexa tudo em poucas conexões reais. É "reutilizar conexões" no único lugar onde dá para compartilhar entre processos | Mais um componente para o escopo do desafio. Fica registrado como o passo seguinte se as réplicas crescerem |
| Subir `max_connections` | Imediato | Cada conexão é um processo do SO com memória própria; centenas custam RAM e troca de contexto, e o throughput piora. Trata o sintoma |
| `Multiplexing=true` do Npgsql | Intercala comandos concorrentes numa mesma conexão física | **Transação explícita não multiplexa** — a conexão fica fixada até o commit. Neutralizado exatamente no `POST /loans`, que é onde a pressão existe |

**Cuidado registrado:** `Minimum Pool Size` fica em `0`. Se fosse `10`, as 11 réplicas
manteriam 110 conexões abertas **em repouso**, estourando o limite sem nenhum tráfego.

---

## 10. Índice de decisões

| § | Decisão | Escolha | Principal alternativa | Inverteria se |
| --- | --- | --- | --- | --- |
| 1.1 | Exemplares | Contador no `Book` | `BookCopy` + `SKIP LOCKED` — mais fiel ao domínio, recusado por proporcionalidade | Identidade do exemplar importar, ou contenção num título virar real |
| 1.3 | Status do empréstimo | `enum` → texto | `int` ou `enum` nativo do PG | Volume tornar o tamanho da coluna relevante |
| 2.1 | Concorrência | `UPDATE` condicional atômico | `SELECT FOR UPDATE`; `SERIALIZABLE` | Decisão envolver mais de um agregado |
| 2.3 | Devolver/cancelar | `UPDATE` guardado por status | Checar-depois-escrever | — |
| 2.4 | Otimista **e** pessimista | Token no `PATCH`, atômico no empréstimo | Uma estratégia só para tudo | Critério é escrita absoluta vs. relativa, não uniformidade |
| 2.5 | Nível de isolamento | `READ COMMITTED` | `REPEATABLE READ` / `SERIALIZABLE` | Uma decisão depender de várias leituras coerentes na mesma transação |
| 3 | Idempotência | Tabela + índice único como mutex | `loans.idempotency_key UNIQUE` | Escopo ser só "não duplicar", sem replay de resposta |
| 4 | Auditoria | Chamada explícita no caso de uso | Interceptor do `SaveChanges` | Precisar cobrir 100% das escritas automaticamente |
| 5 | Cache | Cache-aside, **uma chave** com o snapshot do livro servindo dois endpoints; `DEL` após o commit | Duas chaves por volatilidade; `SET`/`DECR`; write-through; `HybridCache`; cachear a listagem | Disponibilidade ganhar origem/custo próprios (`BookCopy`, §1.1) ou o livro ganhar *joins* |
| 6.1 | Status HTTP | 409 conflito / 422 semântica | 400 para tudo | — |
| 6.5 | Conflito no `PATCH` | 409 sem retry automático, detectado por contagem de linhas | `DbUpdateConcurrencyException` (perderia empréstimo concorrente); retry/mesclagem no servidor; `ETag` + `If-Match` | Cliente precisar de edição condicional canônica |
| 6.2 | `DELETE /books` | Desativa, 409 se ativo | `POST /deactivate` + 405 | Contrato pudesse divergir do sugerido |
| 6.4 | Paginação | Offset | Cursor | Volume crescer |
| 7.2 | Containers de teste | Compartilhados na coleção | Um par por classe | Interferência entre classes gerar teste intermitente |
| 9.1 | Arquitetura | Monólito modular | Microserviços | Perfis de carga/donos divergirem |
| 9.2 | Camadas | 4 projetos | Projeto único com pastas | Escopo permanecer pequeno |
| 9.4 | Casos de uso | Classes diretas | MediatR | Passar de ~20 casos de uso |
| 9.6 | Chave primária | `Guid` v7 no exposto, `bigint` na auditoria | `bigint identity` em tudo | Houver autenticação: id previsível deixa de ser exposição |
| 9.7 | Token de concorrência | Coluna `Version`, só o `PATCH` incrementa | `xmin`; campos descritivos como tokens individuais | Campos descritivos ficarem poucos e estáveis |
| 9.12 | Pool de conexões | `Maximum Pool Size=8` (11 réplicas × 8 = 88 < ~97) | PgBouncer; subir `max_connections`; multiplexing | `maxReplicas` crescer — aí PgBouncer |

---

## 11. Limitações que vão assumidas no README

- **Sem autenticação.** O ator vem de `X-Actor-Id`, autodeclarado: suficiente para a
  trilha de auditoria do desafio, insuficiente para produção e inadequado como fronteira
  de segurança ou escopo de idempotência.
- **Sem expurgo de `idempotency_records`.** `ExpiresAt` está gravado; a limpeza exigiria
  um `Job` agendado.
- **Janela de corrida do cache** (§5) — mitigada por TTL curto, sem impacto em corretude.
- **Cache stampede** não tratado (§5).
- **Paginação por offset** (§6.4).
- **`GET /audit-events` sem restrição de acesso** — em produção, endpoint privilegiado.
- **Teste de concorrência é evidência, não prova** (§7.4).
- **Imagem do `Job` de migrations** ainda não construída (§9.9).
