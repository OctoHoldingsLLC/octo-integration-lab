CREATE TABLE IF NOT EXISTS partner_settings (
    id boolean PRIMARY KEY DEFAULT true CHECK (id),
    mode text NOT NULL DEFAULT 'healthy' CHECK (mode IN ('healthy','reject','slow','offline','lost-ack')),
    delay_ms integer NOT NULL DEFAULT 5000 CHECK (delay_ms BETWEEN 3000 AND 15000)
);
INSERT INTO partner_settings (id) VALUES (true) ON CONFLICT DO NOTHING;

CREATE TABLE IF NOT EXISTS partner_receipts (
    event_id uuid PRIMARY KEY,
    payload_hash char(64) NOT NULL,
    order_id uuid NOT NULL,
    reference varchar(80) NOT NULL,
    received_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    duplicates integer NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS partner_orders (
    order_id uuid PRIMARY KEY,
    event_id uuid NOT NULL UNIQUE REFERENCES partner_receipts(event_id),
    reference varchar(80) NOT NULL,
    sku varchar(32) NOT NULL,
    quantity integer NOT NULL,
    processed_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
