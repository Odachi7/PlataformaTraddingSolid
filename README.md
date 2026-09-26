# Trading Platform — SOLID Refactoring Lab

Projeto **intencionalmente mal arquitetado**, mas funcional, criado para estudar refatoração e SOLID.

A regra principal deste laboratório: **quase todas as responsabilidades estão concentradas em `Program.cs`**. Há validação, SQL, regra de negócio, matching, cálculo de saldo disponível, depth e estatísticas no mesmo arquivo. Não use este desenho como referência para produção — o objetivo é justamente melhorá-lo.

## Requisitos
- .NET 8 SDK
- PostgreSQL 17 (ou Docker)

## Subindo o banco
```bash
docker compose up -d
```

Se usar outro PostgreSQL, execute `schema.sql` e altere `ConnectionStrings:Default` em `appsettings.json`.

## Rodando
```bash
dotnet restore
dotnet run
```

O terminal exibirá a URL local da API.

## Endpoints
- POST `/signup`
- POST `/deposit`
- POST `/withdraw`
- POST `/place_order`
- POST `/cancel_order`
- POST `/execute_order/{orderId}` (endpoint auxiliar para estudar o matching; `place_order` já tenta executar automaticamente)
- GET `/accounts/{accountId}`
- GET `/accounts/{accountId}/orders?status=open`
- GET `/orders/{orderId}`
- GET `/markets/{marketId}`
- GET `/markets/{marketId}/trades`
- GET `/markets/{marketId}/depth?precision=0`

## Exemplos
Criar conta:
```json
POST /signup
{
  "name": "Ryan Rodrigues",
  "email": "ryan@example.com",
  "document": "52998224725",
  "password": "Senha123"
}
```

Depositar:
```json
POST /deposit
{
  "accountId": "UUID",
  "assetId": "USD",
  "quantity": 1000000
}
```

Criar ordem:
```json
POST /place_order
{
  "marketId": "BTC-USD",
  "accountId": "UUID",
  "side": "buy",
  "quantity": 10,
  "price": 83000
}
```

## Pontos intencionalmente ruins para refatorar
- `Program.cs` gigante.
- Endpoints conhecem SQL e Npgsql.
- Regras de domínio misturadas à API.
- Repetição de consultas e validações.
- Dependências concretas criadas diretamente.
- Matching engine no mesmo arquivo dos endpoints.
- Helpers estáticos no mesmo arquivo.
- Strings para status, side, asset e market.
- Transações e persistência acopladas à regra.
- Nenhuma camada Service/Repository.
- Nenhuma interface de domínio.

Comece pelo SRP sem tentar “arrumar tudo” de uma vez. Faça commits pequenos para conseguir comparar cada etapa.
