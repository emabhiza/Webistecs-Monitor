#!/bin/bash

# 🚨 Exit immediately if any command fails
set -e

# 📝 Validate arguments
if [ "$#" -ne 2 ]; then
    echo "❌ Usage: ./run.sh <image_tag> <base_name>"
    echo "Example: ./run.sh latest emabhiza"
    exit 1
fi

# 🚀 Set Variables
IMAGE_TAG=$1         # Image tag: latest, v1.0.0
BASE_NAME=$2         # Docker Hub username or repository owner

# 🎯 Docker Image Name
IMAGE_NAME="${BASE_NAME}/webistecs-monitor:${IMAGE_TAG}"

# 🛠️ Build the Docker image
echo "🚧 Building Docker image: $IMAGE_NAME"
docker build -t "$IMAGE_NAME" .

# 📤 Push the Docker image
echo "📤 Pushing Docker image: $IMAGE_NAME"
docker push "$IMAGE_NAME"

echo "✅ Build and push completed successfully! Image: $IMAGE_NAME"
