# Copilot Instructions

## Project Guidelines
- For media metadata architecture, prefer reading most detailed metadata on demand when an item is selected; persist only common/basic fields needed for search and filtering, and ask the user to choose the exact persisted fields before changing the database schema. 
- For this database redesign, do not add migration logic. User will delete the existing database file and local cache; implement only the fresh database schema. Wait for the user's exact persisted-field list before changing schema.