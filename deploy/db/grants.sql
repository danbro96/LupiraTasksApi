-- Bootstrap role + database for LupiraTasksApi. Placeholder credentials —
-- replace 'CHANGEME' with the real secret before running against a real cluster.
CREATE ROLE tasks LOGIN PASSWORD 'CHANGEME';
CREATE DATABASE tasks_db OWNER tasks;
-- Marten manages its own schema objects under the `tasks` schema at runtime.
