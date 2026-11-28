# Load tests (US-191, D10 §7)

k6 scenarios for the POS path (cart → 15 scans → cash → Finalize) and the oracle check that must follow every run.

| Scenario | How | Pass criteria (D10 §7.2) |
|---|---|---|
| `SMOKE` | 1 sale/s, 1 min — proves the scripts and the fixture | thresholds below |
| `L1` Steady | 13.3 Finalize/s, 30 min (`L4` Soak: `DURATION=8h`) | Finalize p95 < 2 s, p99 < 4 s; addLine p95 < 250 ms; Lookup p95 < 300 ms; errors < 0.1 % |
| `L2` Target | 40/s (3×), 30 min | same |
| `L3` Spike | 66.5/s (5×), 10 min | p95 Finalize < 4 s; back to SLO within 5 min |
| `L5` Hot SKU | `HOT=1`: every cart is one product | no deadlock; lock wait p95 < 200 ms |
| `L6` Reports | L2 + 20 report readers | POS SLOs unchanged |
| `L7` OTP abuse | `k6/otp-abuse.js` | needs `POST /c/auth/otp` (US-141); stops at once until then |

```bash
SCENARIO=SMOKE tests/perf/run-perf.sh      # needs k6, Keycloak, the test database (see the header of run-perf.sh)
```

After every scenario `core.run_oracles()` runs for the perf store; **a pass with a finding is a failure** (D10 §5.13).
A development machine cannot give the SLO verdict: the numbers count on the Perf environment (Prod-sized, D11).
