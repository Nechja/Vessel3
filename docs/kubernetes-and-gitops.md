# Kubernetes & GitOps Operations Guide

Vessel3 provides first-class Kubernetes integration designed for GitOps workflows with ArgoCD and Flux.

## Architecture

The ecosystem consists of:

1. **Vessel3 Server (`ghcr.io/nechja/vessel3`)**:
   - Ultra-fast S3, OCI, Azure Blob, and WebDAV storage engine.
   - Compiled to Native AOT on distroless base images (`runtime-deps:noble-chiseled`).
   - Dedicated low-overhead health endpoints: `/healthz`, `/livez`, `/readyz`.
   - Prometheus metrics endpoint: `/metrics`.

2. **Vessel3 Operator (`ghcr.io/nechja/vessel3-operator`)**:
   - Native AOT Kubernetes controller managing Vessel3 instances and resources.
   - Hexagonal architecture decoupling Kubernetes API operations from Vessel client operations.
   - Zero-reflection source-generated JSON serialization.
   - Reconciles `VesselServer`, `VesselBucket`, and `VesselUser` custom resources.

## Custom Resource Definitions

### 1. VesselServer (`vessel.nechja.io/v1alpha1`)

Provisions a managed Vessel3 instance running as a StatefulSet with persistent storage and admin credentials Secret.

```yaml
apiVersion: vessel.nechja.io/v1alpha1
kind: VesselServer
metadata:
  name: vessel-primary
  namespace: storage
spec:
  replicas: 1
  storage:
    size: 100Gi
    storageClassName: standard
  auth:
    adminSecretName: vessel-primary-admin-creds
  service:
    port: 9000
```

### 2. VesselBucket (`vessel.nechja.io/v1alpha1`)

Declaratively provisions and configures S3 buckets on a target `VesselServer`.

```yaml
apiVersion: vessel.nechja.io/v1alpha1
kind: VesselBucket
metadata:
  name: assets-bucket
  namespace: storage
spec:
  serverRef:
    name: vessel-primary
    namespace: storage
  bucketName: assets
  versioning: Enabled
  access: private
  prunePolicy: Retain
  website:
    indexDocument: index.html
    errorDocument: 404.html
```

### 3. VesselUser (`vessel.nechja.io/v1alpha1`)

Provisions IAM users, issues access keys, and writes credentials into target Kubernetes Secrets with custom key mappings.

```yaml
apiVersion: vessel.nechja.io/v1alpha1
kind: VesselUser
metadata:
  name: backup-agent
  namespace: storage
spec:
  serverRef:
    name: vessel-primary
    namespace: storage
  username: velero-backup
  role: Admin
  writeSecret:
    secretName: velero-s3-creds
    secretNamespace: velero
    keys:
      accessKey: cloud
      secretKey: password
      endpoint: endpoint
      region: region
```

### 4. VesselWebhook (`vessel.nechja.io/v1alpha1`)

Declaratively provisions and reconciles event webhooks on a target `VesselServer`. Supports inline secrets or secure references to Kubernetes `Secret` resources (`secretRef`).

```yaml
apiVersion: vessel.nechja.io/v1alpha1
kind: VesselWebhook
metadata:
  name: container-deploy-hook
  namespace: storage
spec:
  serverRef:
    name: vessel-primary
    namespace: storage
  name: "ArgoCD Webhook"
  url: "https://argo-events.pipeline.svc.cluster.local:12000/vessel"
  secretRef:
    name: webhook-signing-secret
    key: secret-token
  eventFilters:
    - "container.image.pushed"
  resourceFilters:
    - "drummer:*"
  active: true
```

The operator continuously reports webhook ID, last delivery timestamp, and response status directly into `.status`:

```yaml
status:
  phase: Ready
  webhookId: whk_01J8R6T3Y9
  lastTriggeredAt: "2026-10-03T12:05:00Z"
  lastStatusCode: 200
  conditions:
    - type: Ready
      status: "True"
```

## Helm Charts


### Server Chart (`charts/vessel3`)

```bash
helm install vessel3 ./charts/vessel3 \
  --namespace storage \
  --create-namespace \
  --set storage.size=50Gi
```

### Operator Chart (`charts/vessel3-operator`)

```bash
helm install vessel3-operator ./charts/vessel3-operator \
  --namespace vessel3-system \
  --create-namespace
```

## ArgoCD Deployment

Deploy Vessel3 and manage resources declaratively using the ArgoCD manifest in `examples/gitops/argocd-vessel3.yaml`:

```bash
kubectl apply -f examples/gitops/argocd-vessel3.yaml
```
