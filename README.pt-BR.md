# MCP Support Tools · Guardrails para agentes de IA

[![CI](https://github.com/edybackend/mcp-support-tools/actions/workflows/ci.yml/badge.svg)](https://github.com/edybackend/mcp-support-tools/actions/workflows/ci.yml)
[Read in English](README.md)

Um servidor **Model Context Protocol** em C# (.NET 10, SDK oficial do MCP) que dá a um assistente de IA ferramentas sobre
um balcão de suporte: buscar chamados, ler, fazer a triagem, adicionar notas, encerrar. O Claude, ou qualquer cliente
MCP, pode chamá-las.

O ponto não são as ferramentas, e sim **o que o servidor garante** quando um modelo as chama:

| Guardrail | Como |
|---|---|
| Quem pode conectar | chaves de API configuradas só como impressão digital SHA-256, comparadas em tempo constante; sem chave → `401` |
| O que cada cliente pode fazer | escopos por chave (`tickets.read`, `tickets.write`, `audit.read`) como políticas `[Authorize]` nas ferramentas: um cliente só de leitura **nem enxerga** as ferramentas de escrita |
| O que o modelo fica sabendo | anotações MCP em toda ferramenta (`readOnly`, `destructive`, `idempotent`): `close_ticket` é destrutiva, então o cliente pergunta ao usuário antes |
| O que o modelo vê | e-mails e telefones mascarados em todo resultado (`a***@example.com`, `***-4321`) |
| O que o modelo pode escrever | notas com número de cartão (conferido por Luhn) ou credenciais são recusadas, com explicação |
| Quanto | 30 chamadas por minuto por cliente; um agente em loop recebe um erro da ferramenta, não derruba o banco |
| O que aconteceu | toda chamada auditada: cliente, ferramenta, argumentos mascarados, resultado, duração |

## Como rodar

Requisito: Docker.

```bash
docker compose up --build        # endpoint MCP: http://localhost:8080/mcp (Streamable HTTP)
```

Chaves de demonstração (públicas de propósito; só o SHA-256 delas está na configuração):

| Chave | Cliente | Escopos |
|---|---|---|
| `dev-readonly-key-change-me` | readonly-assistant | `tickets.read` |
| `dev-agent-key-change-me` | support-agent | `tickets.read`, `tickets.write`, `audit.read` |

**Com o Claude Code**: esta pasta tem um [`.mcp.json`](.mcp.json) (chave só de leitura), então o `claude` iniciado aqui
oferece o servidor `support-desk`. Para adicionar em outro lugar, com acesso de escrita:

```bash
claude mcp add --transport http support-desk http://localhost:8080/mcp \
  --header "Authorization: Bearer dev-agent-key-change-me"
```

Depois peça: *"Faça a triagem do chamado aberto mais urgente"*. O prompt `triage_ticket` descreve os passos que o balcão espera.

**Com JSON-RPC puro** (o que qualquer cliente envia):

```bash
curl -s localhost:8080/mcp -H 'Authorization: Bearer dev-readonly-key-change-me' \
  -H 'content-type: application/json' -H 'accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_ticket","arguments":{"ticketId":"T-1001"}}}'
```

A auditoria: `curl localhost:8080/audit -H 'Authorization: Bearer dev-agent-key-change-me'`.

<details>
<summary>Sem Docker (.NET 10 SDK)</summary>

```bash
dotnet run --project src/SupportDesk.Mcp   # http://localhost:8080/mcp
```
</details>

## O que o servidor oferece

| Tipo | Nome | Escopo | Anotações |
|---|---|---|---|
| ferramenta | `search_tickets` (text, status, priority, limit) | leitura | só leitura, idempotente |
| ferramenta | `get_ticket` (ticketId) | leitura | só leitura, idempotente |
| ferramenta | `customer_history` (customerId) | leitura | só leitura, idempotente |
| ferramenta | `ticket_stats` | leitura | só leitura, idempotente |
| ferramenta | `add_internal_note` (ticketId, note) | escrita | não idempotente |
| ferramenta | `set_priority` (ticketId, priority) | escrita | idempotente |
| ferramenta | `close_ticket` (ticketId, resolution) | escrita | **destrutiva** |
| recurso | `ticket://{ticketId}` (Markdown) | leitura | |
| prompt | `triage_ticket` (ticketId) | leitura | |

Os resultados são JSON estruturado com schema. Violações de regra voltam como erros da ferramenta escritos para o modelo:
`"ticketId looks like T-1001."`, `"Ticket T-1007 is already solved."`, `"The note contains what looks like a card number…"`.

## Como funciona

```mermaid
flowchart LR
    C[Cliente MCP<br/>Claude, IDE, agente] -->|POST /mcp + chave Bearer| A[Autenticação por chave<br/>→ cliente + escopos]
    A --> F[Filtro de autorização MCP<br/>esconde / recusa ferramentas pelo escopo]
    F --> G[Guarda das chamadas<br/>rate limit → chamada → auditoria]
    G --> T[Ferramentas / recurso / prompt]
    T --> D[Balcão de suporte<br/>regras + visões mascaradas]
    D --> DB[(SQLite)]
    G --> L[(audit_log)]
```

O transporte é **Streamable HTTP stateless**, então qualquer instância atrás de um balanceador pode responder qualquer
requisição. A autenticação é ASP.NET Core comum; os filtros de autorização do SDK transformam `[Authorize(Policy = …)]`
numa classe de ferramentas em "não listada" e "recusada".

## Como é testado

Os testes de integração se conectam com o **cliente MCP oficial em C#**, via Streamable HTTP, ao servidor real:

| Comportamento | Teste |
|---|---|
| Sem chave → conexão recusada | `Without_a_key_the_connection_is_refused` |
| Chave só de leitura lista 4 ferramentas; chave de agente, 7 | `A_read_only_key_sees_only_the_read_tools` |
| Anotações como declaradas | `Annotations_tell_the_client_which_tools_are_safe` |
| Chave só de leitura chamando ferramenta de escrita → proibido | `A_read_only_key_cannot_call_a_write_tool` |
| E-mail e telefone mascarados | `Personal_data_is_masked_in_results` |
| Cartão / senha numa nota → recusada e não gravada | `Notes_with_card_data_or_credentials_are_refused_and_not_stored` |
| Erros que o modelo consegue usar | `Rule_violations_come_back_as_errors_the_model_can_act_on` |
| 31ª chamada no minuto recusada; no minuto seguinte funciona (relógio movido) | `The_31st_call_in_a_minute_is_refused_and_the_next_minute_works` |
| Linhas de auditoria, com `[card]` no lugar do número; `/audit` exige `audit.read` | `Every_call_is_audited_with_redacted_arguments` |
| Recurso e prompt | `The_ticket_resource_and_the_triage_prompt_are_available` |

O CI também sobe o contêiner e fala JSON-RPC puro com ele (401, initialize, 4 × 7 ferramentas, e-mail mascarado, cartão
recusado). Tirar o `AddAuthorizationFilters()` faz 12 de 13 testes de integração falharem: o SDK se recusa a servir
ferramentas com `[Authorize]` sem o filtro, ou seja, é seguro por padrão.

## Tecnologias

| Área | Escolha |
|---|---|
| Protocolo | Model Context Protocol, `ModelContextProtocol.AspNetCore` 2.4 (SDK oficial em C#), Streamable HTTP, stateless |
| Back-end | .NET 10, autenticação e políticas de autorização do ASP.NET Core, Dapper + SQLite |
| Qualidade | xUnit v3 com o cliente MCP oficial, analisadores Roslyn (avisos viram erro), gitleaks |
| Entrega | contêiner sem root, Docker Compose, GitHub Actions |

## Decisões de design

- **Guardrails no servidor, não no prompt.** Um prompt de sistema pode ser ignorado ou sofrer injeção; uma política na ferramenta, não.
- **Menor privilégio já na listagem.** Um cliente que não pode escrever não vê as ferramentas de escrita, então o modelo nem planeja usá-las.
- **Anotações honestas.** São dicas para o cliente, não segurança; a segurança são os escopos. Os dois estão definidos, e um hook torna os dois obrigatórios.
- **Erros são instruções.** Um modelo se recupera de `"ticketId looks like T-1001"`; não se recupera de `"Bad Request"`.
- **Mascarar na origem.** O balcão já devolve visões mascaradas, então nenhuma ferramenta vaza o que nunca recebe.
- **HTTP stateless.** Sem afinidade de sessão; o servidor escala como qualquer API.

## Trade-offs conhecidos

- **Chaves de API, não OAuth.** A especificação do MCP define OAuth 2.1 para servidores remotos; as chaves mantêm a demo autocontida. O SDK suporta o fluxo OAuth quando há um provedor de identidade real.
- **O rate limit é por instância** (em memória). Várias instâncias compartilhariam o limite via Redis ou gateway.
- **A detecção de cartões e segredos por padrão** pega os casos comuns, não todos; um serviço de DLP iria além.
- **Chamadas negadas são recusadas pelo filtro de autorização antes do filtro de auditoria**, então aparecem nos logs do ASP.NET Core, não no `audit_log`.

## Trabalhando com um agente de IA

Este repositório é uma ferramenta para agentes de IA e também foi construído com um. Os hooks dele em [`.claude/`](.claude) exigem que toda ferramenta MCP declare anotações e descrição, que todo tipo MCP tenha política de autorização e que o `AddAuthorizationFilters()` continue no lugar. O [`CLAUDE.md`](CLAUDE.md) explica as regras da superfície MCP.

## Licença

[MIT](LICENSE)
