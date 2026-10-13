ALTER TABLE lab_events ADD COLUMN trace_parent text;
ALTER TABLE lab_events ADD COLUMN trace_state text;
ALTER TABLE lab_attempts ADD COLUMN trace_id text;
