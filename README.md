# ReSharder

Dynamic PostgreSQL shard management operator for Kubernetes.

ReSharder automates horizontal scaling of PostgreSQL databases in Kubernetes clusters. It dynamically manages logical database shards across [CloudNativePG](https://cloudnative-pg.io/) (CNPG) instances, splitting them automatically when disk usage approaches configurable thresholds.

---

## Architecture & Lifecycle

```
                     +-----------------------------------+
                     |           Phase: Idle             |
                     |  - Check PVC disk usage (Kubelet) |
                     +-----------------+-----------------+
                                       |
                   Disk usage >= maxShardSize?
                                       |
                   +-------------------+-------------------+
                   | (shards > 1)                          | (shards == 1)
                   v                                       v
         +--------------------+                  +--------------------+
         |   Execute Split    |                  |  Execute Scale-Up  |
         | - Create CNPG CR   |                  | - Expand PVC size  |
         | - Phase: Migrating |                  | - Remain in Idle   |
         +---------+----------+                  +--------------------+
                   |
                   v
+---------------------------------------------------------------------------------+
|                               Phase: Migrating                                  |
|                                                                                 |
| 1. Replicating:                                                                 |
|    Setup schema (pg_dump) -> Create PUBLICATION / SUBSCRIPTION                  |
|    Poll replication lag until lag < lagThresholdBytes (default 1 MiB)           |
|                                                                                 |
| 2. Draining:                                                                    |
|    Set shard status to 'Maintenance' in ConfigMap (apps stop writes)            |
|    Wait drainWaitSeconds (flush in-flight writes) -> Verify lag == 0            |
|    * Safeguard: if lag != 0 after drainTimeoutSeconds -> auto-rollback          |
|                                                                                 |
| 3. CaughtUp:                                                                    |
|    Synchronize sequence values -> Teardown publication/subscription/slots       |
|                                                                                 |
| 4. CutoverDone:                                                                 |
|    Drop migrated databases from source instance -> Phase: Cleaning              |
+---------------------------------------------------------------------------------+
                                       |
                                       v
                     +-----------------------------------+
                     |          Phase: Cleaning          |
                     | - Wait for disk to be reclaimed   |
                     | - Verify disk < maxShardSize      |
                     | - Transition back to Idle         |
                     +-----------------------------------+
```

---

## Features

- **Automated Horizontal Shard Splitting**: Automatically detects disk saturation on CNPG instances and migrates half the shards to a newly provisioned CNPG cluster.
- **Two-Phase Zero-Downtime Cutover**:
  1. *Lag threshold phase*: Syncs data in background until replication lag is small (< `lagThresholdBytes`).
  2. *Drain phase*: Switches migrating shards to `Maintenance` in the topology ConfigMap, waits `drainWaitSeconds` to flush pending writes, verifies exact zero lag, and performs atomic cutover.
- **Vertical Scaling Fallback**: If an overloaded instance hosts only a single shard, the operator expands the underlying PVC (+20Gi) instead of splitting.
- **Automatic Rollback & Self-Healing**: If replication fails or drain times out, the operator safely tears down replication, cleans up target databases, reverts shard mapping, and restores `Active` status.
- **Topology ConfigMap for Applications**: Dependent deployments read database connection endpoints and shard availability status (`Active` / `Maintenance`) from a managed ConfigMap without requiring pod restarts.
- **Native Kubernetes Events**: Emits detailed events (`ShardSplitStarted`, `DrainStarted`, `CutoverCompleted`, `CleaningCompleted`, `StorageScaledUp`, `MigrationRolledBack`) viewable via `kubectl describe`.
- **Health Probes**: Embedded `/healthz` (liveness) and `/readyz` (readiness) HTTP endpoints on port 8080.
- **Crash Resilience**: Full migration and cleaning state is persisted in `status.activeMigration` and `status.cleaning` to safely resume or roll back after operator restarts.

---

## Repository Structure

```
ReSharder/
|-- src/
|   `-- ReSharder.Operator/
|       |-- Controller/
|       |   `-- ShardManagedDatabaseController.cs  # 5-phase reconcile loop
|       |-- Entities/
|       |   `-- V1Alpha1ShardManagedDatabase.cs    # CRD specification & status
|       |-- Finalizer/
|       |   `-- ShardManagedDatabaseFinalizer.cs   # Cluster cleanup on CR delete
|       |-- Services/
|       |   |-- CnpgClusterManager.cs              # CNPG CR provisioning & scaling
|       |   |-- KubernetesEventPublisher.cs        # Kubernetes event emitter
|       |   |-- LogicalReplicationManager.cs       # PostgreSQL replication engine
|       |   |-- MigrationOrchestrator.cs           # 5-step migration state machine
|       |   |-- OperatorHealthService.cs           # Liveness & readiness probes
|       |   |-- PostgresExecutor.cs                # Pod exec SQL runner
|       |   |-- PvcMonitor.cs                      # Kubelet PVC stats collector
|       |   `-- ShardSplitPlanner.cs               # Shard distribution algorithm
|       |-- Dockerfile
|       `-- Program.cs
|-- tests/
|   |-- ReSharder.Operator.Tests/                  # 64 unit tests (100% passing)
|   `-- ReSharder.E2E/                             # End-to-end integration & zero data loss verification runner
|-- charts/
|   `-- shard-manager/                             # Production-ready Helm chart
|       |-- crds/
|       `-- templates/
|-- examples/
|   `-- sample-shardmanageddatabase.yaml
`-- README.md
```

---

## Custom Resource Definition (CRD)

```yaml
apiVersion: resharder.io/v1alpha1
kind: ShardManagedDatabase
metadata:
  name: production-db
  namespace: default
spec:
  # Disk-usage threshold per CNPG instance that triggers a split
  maxShardSize: "50Gi"

  # Initial PVC size for newly provisioned CNPG cluster instances
  initialStorageSize: "10Gi"

  # Logical database shard names
  shards:
    - s1
    - s2
    - s3
    - s4

  # Deployments that consume shard topology
  dependentDeployments:
    - order-service
    - payment-service

  # Replication lag threshold (bytes) before entering drain mode (default: 1 MiB)
  lagThresholdBytes: 1048576

  # Pause after setting Maintenance before checking zero-lag (default: 5s)
  drainWaitSeconds: 5

  # Max seconds in drain mode before rolling back to prevent write stall (default: 60s)
  drainTimeoutSeconds: 60

  # Max total seconds for migration before automatic rollback (default: 600s)
  migrationTimeoutSeconds: 600

  # Max retry attempts for migration steps (default: 3)
  maxMigrationRetries: 3

  # Max seconds to wait for disk reclamation in Cleaning phase (default: 120s)
  cleaningTimeoutSeconds: 120
```

---

## Application Topology Integration

The operator maintains a ConfigMap named `app-shard-topology-<cr-name>` in the same namespace:

```yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: app-shard-topology-production-db
data:
  s1.host: smd-instance-production-db-1-rw.default.svc
  s1.port: "5432"
  s1.database: s1
  s1.status: Active
  s2.host: smd-instance-production-db-1-rw.default.svc
  s2.port: "5432"
  s2.database: s2
  s2.status: Active
  s3.host: smd-instance-production-db-2-rw.default.svc
  s3.port: "5432"
  s3.database: s3
  s3.status: Maintenance # Writes temporarily paused during cutover
```

Applications mount this ConfigMap (or watch it via Spring Cloud Kubernetes, Kubernetes client, or Viper) to route queries to the correct host and pause writes when `status == Maintenance`.

---

## Quick Start & Verification (Kind / Minikube)

### 1. Prerequisites

- Kubernetes cluster (Kind, Minikube, or bare-metal)
- CloudNativePG operator installed:
  ```bash
  kubectl apply --server-side -f \
    https://raw.githubusercontent.com/cloudnative-pg/cloudnative-pg/main/releases/cnpg-1.25.0.yaml
  ```
- Helm 3 & .NET 10 SDK

### 2. Install Operator via Helm

```bash
helm install shard-manager ./charts/shard-manager \
  --namespace resharder-system \
  --create-namespace
```

### 3. Deploy a ShardManagedDatabase

```bash
kubectl apply -f examples/sample-shardmanageddatabase.yaml
```

Inspect the database and observed topology:
```bash
kubectl get smd
kubectl describe smd my-app-db
kubectl get cm app-shard-topology-my-app-db -o yaml
```

### 4. Observe Events

```bash
kubectl get events --field-selector involvedObject.kind=ShardManagedDatabase --watch
```

---

## Development & Testing

### Run Unit Tests

```bash
dotnet test --verbosity normal
```

All 64 unit tests execute in under 1 second without external cluster dependencies.

### Run End-to-End Validation Test (Automated Data Loss Verification)

When connected to a test Kubernetes cluster (with CNPG and ReSharder running):

```bash
dotnet run --project tests/ReSharder.E2E -- --namespace default --cr-name e2e-test-db
```

This automated runner:
1. Provisions test shards `[s1, s2, s3, s4]` with `maxShardSize: 30Mi`.
2. Seeds initial test data and records baseline cryptographic checksums (MD5) and row counts.
3. Generates heavy writes into shard `s4` to trip the disk usage threshold.
4. Monitors lifecycle transitions: `Idle` -> `Migrating` (`Replicating` -> `Draining` -> `CaughtUp` -> `CutoverDone`) -> `Cleaning` -> `Idle`.
5. Verifies that migrating shards were placed into `Maintenance` during the cutover window.
6. Performs byte-for-byte MD5 verification across both instances, verifies sequence synchronization without ID collisions, and confirms clean database drops on the source instance.

### Run Operator Locally Against a Cluster

```bash
dotnet run --project src/ReSharder.Operator
```

---

## License

[MIT](LICENSE)
