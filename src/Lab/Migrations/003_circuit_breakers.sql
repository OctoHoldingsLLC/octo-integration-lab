CREATE TABLE lab_circuits (
    run_id uuid NOT NULL REFERENCES lab_runs(id),
    destination text NOT NULL,
    state text NOT NULL DEFAULT 'Closed' CHECK (state IN ('Closed','Open','HalfOpen')),
    failures integer NOT NULL DEFAULT 0,
    open_until timestamptz,
    probe_token uuid,
    probe_lease_until timestamptz,
    PRIMARY KEY(run_id,destination)
);
CREATE TABLE lab_circuit_history (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    run_id uuid NOT NULL REFERENCES lab_runs(id),
    destination text NOT NULL,
    state text NOT NULL,
    reason text NOT NULL,
    at timestamptz NOT NULL DEFAULT clock_timestamp()
);
