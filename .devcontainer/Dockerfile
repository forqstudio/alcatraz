FROM mcr.microsoft.com/devcontainers/base:ubuntu-24.04

LABEL maintainer="Alcatraz Project"
LABEL description="Dev container for the Alcatraz platform (.NET 8 + Go 1.25 + kernel/rootfs build deps)"
LABEL version="2.0.0"

ARG GO_VERSION=1.25.3
ARG DEBIAN_FRONTEND=noninteractive

RUN apt-get update && apt-get install -y --no-install-recommends \
        build-essential bc bison flex cpio kmod libelf-dev libssl-dev rsync tar xz-utils \
        ca-certificates curl wget gpg debootstrap shellcheck \
        git sudo jq openssh-client iproute2 iputils-ping unzip make pkg-config \
        dotnet-sdk-8.0 \
    && rm -rf /var/lib/apt/lists/*

RUN curl -fsSL "https://go.dev/dl/go${GO_VERSION}.linux-amd64.tar.gz" \
        | tar -C /usr/local -xz \
 && printf 'export PATH=$PATH:/usr/local/go/bin:$HOME/go/bin\nexport GOPATH=$HOME/go\n' \
        > /etc/profile.d/go.sh \
 && chmod 0644 /etc/profile.d/go.sh

ENV PATH=/usr/local/go/bin:/home/vscode/go/bin:${PATH} \
    GOPATH=/home/vscode/go \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1

USER vscode
