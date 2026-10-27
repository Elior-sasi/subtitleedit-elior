#!/usr/bin/env bash
# Deploys the subs-il MFA alignment service to the user's own Google Cloud project.
# Default mode is --plan: prints resources and the cost estimate, calls nothing.
set -euo pipefail
project="${1:-}"; mode="${2:---plan}"; tokens="${3:-}"
if [[ ! "$project" =~ ^[a-z][a-z0-9-]{4,28}[a-z0-9]$ ]] || [[ "$mode" != --plan && "$mode" != --deploy ]]; then
  echo 'Usage: bash deploy/deploy.sh PROJECT_ID [--plan | --deploy TOKENS_JSON]' >&2; exit 2
fi
region=us-central1
service=subs-il-mfa
repo=subs-il-mfa
bucket="${project}-subs-il-mfa-jobs"
secret=subs-il-mfa-tokens
sa="subs-il-mfa-run@${project}.iam.gserviceaccount.com"
cat <<PLAN
Project: $project   Region: $region
Creates (if missing): Artifact Registry repo "$repo", private bucket gs://$bucket
  (uniform access, public access prevention, objects deleted after 1 day as a backstop;
  the service itself deletes audio/script when a job ends and results after 6 hours),
  secret "$secret" (SHA-256 hashes of access tokens only), service account $sa with
  access to that bucket and that secret only, Cloud Run service "$service".
Cloud Run: 2 vCPU, 8 GiB, instance-based billing, min 0 / max 1 instance, 1 alignment at a
  time, request timeout 15 min, gen2 sandbox. The URL is reachable from the internet; every
  API call except /healthz requires one of your bearer tokens (app-level authentication).
Cost estimate (us-central1 list prices, checked 2026-10-25): an active instance costs about
  2*0.000018 + 8*0.000002 = 0.000052 USD/s = ~0.19 USD/hour, including up to ~15 idle minutes
  before scale-to-zero. Monthly free tier (shared per billing account): 240,000 vCPU-s and
  450,000 GiB-s, roughly 15 instance-hours at this size. Plus Cloud Build minutes, image
  storage (~3 GB, Artifact Registry ~0.10 USD/GB-month beyond the free 0.5 GB), tiny GCS and
  Secret Manager use. This is an estimate, NOT a spending cap. Billing is not changed here.
PLAN
[[ "$mode" == --plan ]] && exit 0
[[ -f "$tokens" ]] || { echo 'Provide TOKENS_JSON created with: python -m mfa_service.admin USER' >&2; exit 2; }
python3 -c 'import json,re,sys; d=json.load(open(sys.argv[1])); assert d and all(re.fullmatch("[0-9a-f]{64}",k) and re.fullmatch("[a-z0-9_-]{1,32}",v) for k,v in d.items())' "$tokens" \
  || { echo 'TOKENS_JSON must map sha256 hex -> user id (no plain tokens).' >&2; exit 2; }
read -r -p 'Type the project ID to approve these resources and possible charges: ' approved
[[ "$approved" == "$project" ]] || { echo 'Cancelled.'; exit 3; }
here="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
billing="$(gcloud billing projects describe "$project" --format='value(billingEnabled)')"
[[ "$billing" == True || "$billing" == true ]] || { echo 'Billing is not enabled on this project. Enable it yourself first.' >&2; exit 5; }
gcloud services enable run.googleapis.com artifactregistry.googleapis.com cloudbuild.googleapis.com \
  secretmanager.googleapis.com storage.googleapis.com --project="$project"
gcloud artifacts repositories describe "$repo" --location="$region" --project="$project" >/dev/null 2>&1 || \
  gcloud artifacts repositories create "$repo" --repository-format=docker --location="$region" --project="$project"
if ! gcloud storage buckets describe "gs://$bucket" --project="$project" >/dev/null 2>&1; then
  gcloud storage buckets create "gs://$bucket" --project="$project" --location="$region" \
    --uniform-bucket-level-access --public-access-prevention
fi
lifecycle="$(mktemp)"; trap 'rm -f "$lifecycle"' EXIT
echo '{"rule":[{"action":{"type":"Delete"},"condition":{"age":1}}]}' > "$lifecycle"
gcloud storage buckets update "gs://$bucket" --lifecycle-file="$lifecycle" --public-access-prevention --project="$project"
gcloud iam service-accounts describe "$sa" --project="$project" >/dev/null 2>&1 || \
  gcloud iam service-accounts create subs-il-mfa-run --display-name='subs-il MFA service' --project="$project"
gcloud storage buckets add-iam-policy-binding "gs://$bucket" --member="serviceAccount:$sa" --role=roles/storage.objectUser --project="$project"
if gcloud secrets describe "$secret" --project="$project" >/dev/null 2>&1; then
  gcloud secrets versions add "$secret" --data-file="$tokens" --project="$project"
else
  gcloud secrets create "$secret" --data-file="$tokens" --replication-policy=automatic --project="$project"
fi
gcloud secrets add-iam-policy-binding "$secret" --member="serviceAccount:$sa" --role=roles/secretmanager.secretAccessor --project="$project"
image="${region}-docker.pkg.dev/${project}/${repo}/service:mfa-3.4.3"
gcloud builds submit "$here" --tag="$image" --project="$project"
digest="$(gcloud artifacts docker images describe "$image" --project="$project" --format='value(image_summary.digest)')"
[[ "$digest" =~ ^sha256:[a-f0-9]{64}$ ]] || { echo 'Missing verified registry digest.' >&2; exit 6; }
gcloud run deploy "$service" --image="${image%:*}@${digest}" --project="$project" --region="$region" \
  --service-account="$sa" --cpu=2 --memory=8Gi --no-cpu-throttling --min-instances=0 --max-instances=1 \
  --concurrency=20 --timeout=900 --execution-environment=gen2 --allow-unauthenticated \
  --set-env-vars="MFA_STORAGE=gcs,MFA_BUCKET=$bucket,MFA_TOKENS_FILE=/secrets/tokens.json,MFA_MAX_CONCURRENT_JOBS=1,MFA_RETENTION_HOURS=6" \
  --set-secrets="/secrets/tokens.json=${secret}:latest" \
  --startup-probe=httpGet.path=/healthz,initialDelaySeconds=5,periodSeconds=10,failureThreshold=30
url="$(gcloud run services describe "$service" --project="$project" --region="$region" --format='value(status.url)')"
echo "Deployed by immutable digest $digest"
echo "Service URL (paste into Subtitle Edit): $url"
echo "Health check: curl -fsS $url/healthz"

