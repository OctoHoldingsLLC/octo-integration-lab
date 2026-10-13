CREATE TABLE lab_runs (
    id uuid PRIMARY KEY,
    request_key text NOT NULL UNIQUE,
    scenario text NOT NULL,
    batch_size integer NOT NULL CHECK (batch_size BETWEEN 1 AND 50),
    paused boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE TABLE lab_events (
    id uuid PRIMARY KEY,
    order_id uuid NOT NULL REFERENCES lab_orders(id),
    run_id uuid REFERENCES lab_runs(id),
    payload text NOT NULL,
    version integer NOT NULL DEFAULT 1,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE(id, order_id)
);
INSERT INTO lab_events(id,order_id,payload,created_at)
    SELECT id,order_id,payload,created_at FROM lab_deliveries;
ALTER TABLE lab_deliveries ADD COLUMN event_id uuid;
UPDATE lab_deliveries SET event_id=id;
ALTER TABLE lab_deliveries ALTER COLUMN event_id SET NOT NULL;
ALTER TABLE lab_deliveries ADD COLUMN destination text NOT NULL DEFAULT 'primary'
    CHECK (destination IN ('primary','secondary'));
ALTER TABLE lab_deliveries DROP CONSTRAINT lab_deliveries_order_id_key;
ALTER TABLE lab_deliveries ADD CONSTRAINT delivery_event_order FOREIGN KEY(event_id,order_id) REFERENCES lab_events(id,order_id);
ALTER TABLE lab_deliveries ADD CONSTRAINT delivery_destination UNIQUE(event_id,destination);
ALTER TABLE lab_deliveries DROP COLUMN payload;
CREATE INDEX lab_events_run ON lab_events(run_id);
CREATE INDEX lab_deliveries_feed ON lab_deliveries(created_at DESC,id);

CREATE FUNCTION preserve_lab_event() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.payload IS DISTINCT FROM OLD.payload OR NEW.id IS DISTINCT FROM OLD.id
        OR NEW.order_id IS DISTINCT FROM OLD.order_id OR NEW.version IS DISTINCT FROM OLD.version
        OR NEW.run_id IS DISTINCT FROM OLD.run_id THEN
        RAISE EXCEPTION 'Event identity and signed payload are immutable';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER immutable_lab_event BEFORE UPDATE ON lab_events FOR EACH ROW EXECUTE FUNCTION preserve_lab_event();
