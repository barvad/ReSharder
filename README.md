# ReSharder

Dynamic PostgreSQL shard management operator for Kubernetes.

ReSharder automates horizontal scaling of PostgreSQL databases in on-premise Kubernetes clusters. It manages logical database shards across [CloudNativePG](https://cloudnative-pg.io/) instances, splitting them automatically when disk usage approaches configurable thresholds.

## How It Works

1. **Deploy** the operator and create a single `ShardManagedDatabase` custom resource listing your logical shards.
2. **All shards start** on one CNPG cluster instance sharing a single PVC.
3. **When the disk fills up** (reaches `maxShardSize`), the operator splits the instance in half — moving half the shards to a brand-new CNPG cluster via logical replication.
4. **If only one shard remains** on an instance, vertical scaling kicks in — the PVC is expanded instead.
5. **Traffic switching** is zero-downtime: a `ConfigMap`-based topology map lets applications react to migrations without pod restarts.

## Repository Structure

```
ReSharder/
├── charts/
│   └── shard-manager/          # Helm chart (CRD + future operator manifests)
│       ├── Chart.yaml
│       ├── values.yaml
│       ├── crds/
│       │   └── shardmanageddatabase.yaml   # ShardManagedDatabase CRD
│       └── templates/
│           ├── _helpers.tpl
│           └── NOTES.txt
├── examples/
│   └── sample-shardmanageddatabase.yaml    # Example CR
├── .github/
│   └── workflows/
│       └── release-charts.yml              # OCI Helm publish on tag push
├── LICENSE
└── README.md
```

## Custom Resource: ShardManagedDatabase

```yaml
apiVersion: resharder.io/v1alpha1
kind: ShardManagedDatabase
metadata:
  name: my-app-db
spec:
  maxShardSize: "50Gi"        # Split trigger threshold
  initialStorageSize: "10Gi"  # PVC size for new instances
  shards:                     # Logical databases to manage
    - s1
    - s2
    - s3
    - s4
  dependentDeployments:       # Apps that consume shard topology
    - my-backend-api
```

### Status Fields

| Field               | Type                | Description                                              |
|---------------------|---------------------|----------------------------------------------------------|
| `phase`             | `Idle \| Migrating \| Cleaning` | Current operator lifecycle phase              |
| `shardMapping`      | `map[string]string` | Shard name → CNPG cluster instance name                  |
| `observedGeneration`| `int64`             | Last reconciled `metadata.generation`                    |
| `conditions`        | `[]Condition`       | Standard Kubernetes conditions                           |

## Installation

### From OCI Registry (after first release)

```bash
helm install shard-manager \
  oci://ghcr.io/barvad/charts/shard-manager \
  --version 0.1.0
```

### From Source

```bash
git clone https://github.com/barvad/ReSharder.git
cd ReSharder
helm install shard-manager ./charts/shard-manager
```

### Verify the CRD

```bash
kubectl get crd shardmanageddatabases.resharder.io
kubectl get smd   # short name
```

## Releasing a New Chart Version

Push a semver tag to trigger the CI pipeline:

```bash
git tag v0.1.0
git push origin v0.1.0
```

The GitHub Actions workflow will:
1. Lint the chart
2. Package it with the tag version
3. Push to `oci://ghcr.io/barvad/charts/shard-manager`

## Roadmap

- [x] **Iteration 1** — CRD definition, Helm chart, OCI publish pipeline
- [ ] **Iteration 2** — .NET operator scaffold, reconciliation loop, CNPG cluster creation
- [ ] **Iteration 3** — PVC monitoring, split trigger logic
- [ ] **Iteration 4** — Logical replication engine (PUBLICATION / SUBSCRIPTION)
- [ ] **Iteration 5** — Traffic management (ConfigMap topology, zero-downtime cutover)
- [ ] **Iteration 6** — Vertical scaling fallback (single-shard PVC resize)
- [ ] **Iteration 7** — Cleanup phase, end-to-end tests

## License

[MIT](LICENSE)
