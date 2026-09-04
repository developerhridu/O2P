# Oracle to PostgreSQL Database Migration (O2P) - DBA Grants

This document outlines the minimum privileges required by the O2P migration tool service accounts.

## Oracle (Source Database)
The O2P agent requires access to read the source tables, dictionary views, and execute parallel queries.

```sql
-- Replace 'o2p_user' with your actual service account name

-- Connect privilege
GRANT CREATE SESSION TO o2p_user;

-- Dictionary access
GRANT SELECT ANY DICTIONARY TO o2p_user;
-- OR specifically:
GRANT SELECT ON SYS.DBA_TABLES TO o2p_user;
GRANT SELECT ON SYS.DBA_TAB_COLS TO o2p_user;
GRANT SELECT ON SYS.DBA_PART_TABLES TO o2p_user;
GRANT SELECT ON SYS.DBA_TAB_PARTITIONS TO o2p_user;
GRANT SELECT ON SYS.DBA_TAB_SUBPARTITIONS TO o2p_user;

-- Table access (If migrating entire database)
GRANT SELECT ANY TABLE TO o2p_user;
```

## PostgreSQL (Target Database)
The O2P agent requires access to create schemas, tables, and write data.

```sql
-- Ensure user is created
CREATE USER o2p_writer WITH PASSWORD 'secure_password';

-- Connect privilege
GRANT CONNECT ON DATABASE target_db TO o2p_writer;

-- Schema usage and creation (assuming target schema is "public" or explicitly created)
GRANT CREATE, USAGE ON SCHEMA target_schema TO o2p_writer;

-- Since the agent uses COPY, it doesn't need superuser for binary COPY.
```
