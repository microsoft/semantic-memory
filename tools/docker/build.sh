#!/usr/bin/env bash

set -e

DOCKER_IMAGE="kernel-memory/service"
CONFIGURATION=Release

# Change current dir to repo root
HERE="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && pwd)"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && cd ../.. && pwd)"
cd $ROOT

check_dependency_docker() {
    set +e
    TEST=$(which docker)
    if [[ -z "$TEST" ]]; then
        echo "🔥 ERROR: 'docker' command not found."
        echo "Install docker CLI and make sure the 'docker' command is in the PATH."
        exit 1
    fi
    set -e
}

uuid()
{
    local N B T
    for (( N=0; N < 16; ++N ))
    do
        B=$(( $RANDOM%255 ))
        if (( N == 6 ))
        then
            printf '4%x' $(( B%15 ))
        elif (( N == 8 ))
        then
            local C='89ab'
            printf '%c%x' ${C:$(( $RANDOM%${#C} )):1} $(( B%15 ))
        else
            printf '%02x' $B
        fi
    done
    echo
}


build_docker_image() {
    echo "⏱️  Building Docker image..."
    cd $HERE
    DOCKER_TAG1="${DOCKER_IMAGE}:latest"
    DOCKER_TAGU="${DOCKER_IMAGE}:$(uuid)"
    
    #docker build --compress --tag "$DOCKER_TAG1" --tag "$DOCKER_TAGU" \
    #  --build-arg="SOURCE=https://github.com/dluc/kernel-memory" \
    #  --build-arg="BRANCH=docker" .
    
    docker build --compress --tag "$DOCKER_TAG1" --tag "$DOCKER_TAGU" .
    
    # Read versions details (removing \r char)
    SHORT_DATE=$(docker run -it --rm -a stdout --entrypoint cat "$DOCKER_TAGU" .SHORT_DATE)
    SHORT_DATE="${SHORT_DATE%%[[:cntrl:]]}"
    SHORT_COMMIT_ID=$(docker run -it --rm -a stdout --entrypoint cat "$DOCKER_TAGU" .SHORT_COMMIT_ID)
    SHORT_COMMIT_ID="${SHORT_COMMIT_ID%%[[:cntrl:]]}"
    
    # Add version tag
    DOCKER_TAG3="${DOCKER_IMAGE}:${SHORT_DATE}.${SHORT_COMMIT_ID}"
    docker tag $DOCKER_TAGU $DOCKER_TAG3
    docker rmi $DOCKER_TAGU
    
    echo -e "\n\n✅  Docker image ready:"
    echo -e " - $DOCKER_TAG1"
    echo -e " - $DOCKER_TAG3"
}

howto_test() {
  echo -e "\nTo test the image with OpenAI:\n"
  echo "  docker run -it --rm -e OPENAI_DEMO=\"...OPENAI API KEY...\" kernel-memory/service"
  
  echo -e "\nTo test the image with your local config:\n"
  echo "  docker run -it --rm -v ./service/Service/appsettings.Development.json:/app/data/appsettings.json kernel-memory/service"
  
  echo -e "\nTo inspect the image content:\n"
  echo "  docker run -it --rm -v ./service/Service/appsettings.Development.json:/app/data/appsettings.json --entrypoint /bin/sh kernel-memory/service"
  
  echo ""
}

echo "⏱️  Checking dependencies..."
check_dependency_docker

build_docker_image
howto_test
