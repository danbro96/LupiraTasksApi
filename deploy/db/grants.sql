-- deploy/db/grants.sql for lupira-tasks-api — provisions the role + database on the
-- shared `medelynas-db` Postgres (see DevOps/Guides/shared-postgres-platform.md).
-- Role is suffixed `_user` because the bare DB name `lupira_tasks` would collide.
-- Pass the real secret via `psql -v app_password="'<rand>'"` when applying; never commit it.
CREATE ROLE lupira_tasks_user WITH LOGIN PASSWORD :'app_password';
CREATE DATABASE lupira_tasks OWNER lupira_tasks_user;
REVOKE ALL ON DATABASE lupira_tasks FROM PUBLIC;
GRANT CONNECT ON DATABASE lupira_tasks TO lupira_tasks_user;
-- Marten creates + owns its schema objects under the `tasks` schema on first app boot.
