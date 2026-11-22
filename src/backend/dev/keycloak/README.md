# Keycloak realms (dev / spike SP-01)

`merchant-realm.json` and `platform-realm.json` are imported at startup (`--import-realm`). Values implement D06 §7:
10-minute access tokens, refresh rotation with reuse detection, PKCE S256 mandatory for the BFF clients, password grant
disabled, audience mappers (`merchant-api`, `platform-api`), brute-force protection (temporary lock after 10 failures),
password policy (12+ chars), Persian default locale. **The client secret and test users are for development only.**

Run the checks (17) against a local Keycloak:

```bash
docker run -d --name rc-kc -p 8080:8080 -e KC_BOOTSTRAP_ADMIN_USERNAME=admin -e KC_BOOTSTRAP_ADMIN_PASSWORD=admin \
  -v "$PWD:/opt/keycloak/data/import:ro" keycloak/keycloak:26.0 start-dev --import-realm
KC=http://127.0.0.1:8080 NODE_PATH=$(npm root -g) node spike-sp01.js
```

Customer login is intentionally not a Keycloak realm here: see the proposed amendment in ADR-003 (needs approval).
