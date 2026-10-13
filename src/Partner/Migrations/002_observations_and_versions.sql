ALTER TABLE partner_orders ADD COLUMN version integer NOT NULL DEFAULT 1;
ALTER TABLE partner_orders ADD COLUMN status text NOT NULL DEFAULT 'confirmed';
ALTER TABLE partner_receipts ADD COLUMN effect text NOT NULL DEFAULT 'Applied';
ALTER TABLE partner_receipts ADD COLUMN run_id uuid;
CREATE TABLE partner_requests (
    event_id uuid PRIMARY KEY,
    requests integer NOT NULL DEFAULT 1,
    first_seen_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE TABLE partner_run_controls (
    run_id uuid PRIMARY KEY,
    mode text NOT NULL CHECK (mode IN ('healthy','reject'))
);
CREATE TABLE partner_observations (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    event_id uuid NOT NULL,
    run_id uuid,
    observed_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    outcome text NOT NULL,
    detail text NOT NULL,
    http_status integer,
    trace_id text
);
CREATE INDEX partner_observations_event ON partner_observations(event_id,id);
CREATE INDEX partner_observations_run ON partner_observations(run_id,id);
