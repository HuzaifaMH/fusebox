-- Runs once, when the database is first created.
--
-- Grafana reads the request, rollup, anomaly and alert tables, and has no reason to change them, so it gets its own
-- role that can only read. Fusebox creates those tables later, as the user that runs this script, so the default
-- privileges below are what make them readable to Grafana the moment they exist.
CREATE ROLE grafana LOGIN PASSWORD 'demo-only-grafana-password';

GRANT CONNECT ON DATABASE fusebox TO grafana;
GRANT USAGE ON SCHEMA public TO grafana;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO grafana;
