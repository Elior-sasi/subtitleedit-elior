#!/usr/bin/env bash
# Run from Google Cloud Shell. Default is a plan: no login, APIs, billing or writes.
set -euo pipefail
project="${1:-}"
mode="${2:---plan}"
if [[ ! "$project" =~ ^[a-z][a-z0-9-]{4,28}[a-z0-9]$ ]] || [[ "$mode" != --plan && "$mode" != --deploy-probe ]]; then
  echo 'Usage: bash setup.sh PROJECT_ID [--plan|--deploy-probe]' >&2; exit 2
fi
region=us-central1
repo=subs-il-mfa-setup
job=subs-il-mfa-probe
service_account="subs-il-mfa-probe@${project}.iam.gserviceaccount.com"
cat <<EOF
Project: $project
Region: $region
Action: build official MFA 3.4.3 and deploy a PRIVATE setup probe.
CPU: 1; RAM: 4 GiB; tasks: 1; retries: 0; timeout: 10 minutes.
No GPU, public endpoint, automatic schedule or subtitle/audio upload.
Does not execute the Cloud Run job automatically.
Requires an existing billing-enabled project. Build/image storage/compute can cost
money; monthly free quotas do not guarantee a zero bill. This is not a spend cap.
No production alignment or desktop connection is created by this probe.
EOF
[[ "$mode" == --plan ]] && exit 0
read -r -p 'Type the project ID to approve these cloud resources and possible charges: ' approved
[[ "$approved" == "$project" ]] || { echo 'Cancelled.'; exit 3; }
command -v gcloud >/dev/null
here="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
[[ -f "$here/Dockerfile" && -f "$here/probe.py" ]] || exit 4
billing_enabled="$(gcloud billing projects describe "$project" --format='value(billingEnabled)')"
[[ "$billing_enabled" == True || "$billing_enabled" == true ]] || { echo 'Enable project billing yourself first.' >&2; exit 5; }
gcloud services enable run.googleapis.com artifactregistry.googleapis.com cloudbuild.googleapis.com --project="$project"
if ! gcloud artifacts repositories describe "$repo" --location="$region" --project="$project" >/dev/null 2>&1; then
  gcloud artifacts repositories create "$repo" --repository-format=docker --location="$region" --project="$project"
fi
if ! gcloud iam service-accounts describe "$service_account" --project="$project" >/dev/null 2>&1; then
  gcloud iam service-accounts create subs-il-mfa-probe --display-name='Subtitle Edit MFA setup probe (no data access)' --project="$project"
fi
# Intentionally grant no project or storage roles to the runtime identity.
image="${region}-docker.pkg.dev/${project}/${repo}/mfa:setup-3.4.3"
gcloud builds submit "$here" --tag="$image" --project="$project"
digest="$(gcloud artifacts docker images describe "$image" --project="$project" --format='value(image_summary.digest)')"
[[ "$digest" =~ ^sha256:[a-f0-9]{64}$ ]] || { echo 'Missing verified registry digest.' >&2; exit 6; }
gcloud run jobs deploy "$job" --image="${image%:*}@${digest}" --project="$project" --region="$region" \
  --service-account="$service_account" --cpu=1 --memory=4Gi --tasks=1 --parallelism=1 --max-retries=0 --task-timeout=10m
echo 'Probe deployed by immutable image digest. It has NOT run yet.'
echo "Run once: gcloud run jobs execute $job --project=$project --region=$region --wait"
echo 'A successful CLI probe is not an alignment-quality or full-film test.'

