# Private item roulette

Base: local Anomaly Item Upgrader 0.2.5, Hardcoon, MIT.

Bridge integration revision: 0.2.5-adb.1 / addon 1.0.120.

The original standalone addon is not overwritten. The five Lua modules,
localisation, UI texture IDs, sound/atlas paths and save keys use private names.
No F6 binding or plugin configuration is installed. The main/UI modules add
an explicit request for 1–50 attempts and `both`, `low` or `high` risk policy;
single-risk mode hides both native risk buttons and their separate captions.
Each attempt is initiated and acknowledged by the player. Subsequent attempts
reopen the modal without silently spinning. Pending requests, risk policy,
series progress and transactions survive supported game saves.

The catalog, transaction logic and art geometry are unchanged apart from names.
The DDS atlas and two OGG files are byte-identical to 0.2.5. See LICENSE and
LICENSE_AUDIO.txt for upstream terms and original audio filenames/attribution.
