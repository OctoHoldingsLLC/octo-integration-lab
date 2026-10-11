CREATE TABLE IF NOT EXISTS lab_settings (
    id boolean PRIMARY KEY DEFAULT true CHECK (id),
    paused boolean NOT NULL DEFAULT false
);
INSERT INTO lab_settings (id) VALUES (true) ON CONFLICT DO NOTHING;

CREATE TABLE IF NOT EXISTS lab_orders (
    id uuid PRIMARY KEY,
    request_key varchar(128) NOT NULL UNIQUE,
    request_hash char(64) NOT NULL,
    reference varchar(80) NOT NULL,
    sku varchar(32) NOT NULL,
    quantity integer NOT NULL CHECK (quantity BETWEEN 1 AND 100),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE TABLE IF NOT EXISTS lab_deliveries (
    id uuid PRIMARY KEY,
    order_id uuid NOT NULL UNIQUE REFERENCES lab_orders(id),
    payload text NOT NULL,
    status text NOT NULL DEFAULT 'Pending' CHECK (status IN ('Pending','Delivering','Retrying','Delivered','Exhausted')),
    attempts integer NOT NULL DEFAULT 0,
    cycle_attempts integer NOT NULL DEFAULT 0,
    replays integer NOT NULL DEFAULT 0,
    next_attempt_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    claim_token uuid,
    lease_until timestamptz,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE INDEX IF NOT EXISTS lab_deliveries_due ON lab_deliveries(next_attempt_at)
    WHERE status IN ('Pending','Retrying','Delivering');

CREATE TABLE IF NOT EXISTS lab_attempts (
    id uuid PRIMARY KEY,
    delivery_id uuid NOT NULL REFERENCES lab_deliveries(id),
    number integer NOT NULL,
    started_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    completed_at timestamptz,
    outcome text NOT NULL DEFAULT 'Sending',
    http_status integer,
    duration_ms integer,
    detail text,
    UNIQUE (delivery_id, number)
);
