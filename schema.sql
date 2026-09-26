create schema if not exists app;

create table if not exists app.account (
    account_id uuid primary key,
    name text not null,
    email text not null unique,
    document text not null,
    password text not null
);

create table if not exists app.balance (
    account_id uuid not null references app.account(account_id),
    asset_id text not null,
    quantity numeric not null default 0,
    primary key (account_id, asset_id)
);

create table if not exists app."order" (
    order_id uuid primary key,
    market_id text not null,
    account_id uuid not null references app.account(account_id),
    side text not null,
    quantity numeric not null,
    price numeric not null,
    fill_quantity numeric not null default 0,
    fill_price numeric not null default 0,
    status text not null,
    timestamp timestamptz not null
);

create table if not exists app.trade (
    trade_id uuid primary key,
    market_id text not null,
    buy_order_id uuid not null references app."order"(order_id),
    sell_order_id uuid not null references app."order"(order_id),
    side text not null,
    quantity numeric not null,
    price numeric not null,
    timestamp timestamptz not null
);

create index if not exists ix_order_market_status_side_price on app."order" (market_id, status, side, price, timestamp);
create index if not exists ix_order_account on app."order" (account_id, timestamp desc);
create index if not exists ix_trade_market on app.trade (market_id, timestamp desc);
