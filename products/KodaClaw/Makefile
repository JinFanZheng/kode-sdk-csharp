# KodaClaw developer convenience Makefile

ROOT := $(CURDIR)
SOLUTION := $(ROOT)/KodaClaw.sln
WEB_DIR := $(ROOT)/apps/kodaclaw-web
DESKTOP_DIR := $(ROOT)/apps/kodaclaw-desktop
GATEWAY_PROJECT := $(ROOT)/src/KodaClaw.Gateway/KodaClaw.Gateway.csproj
CLI_PROJECT := $(ROOT)/tools/KodaClaw.Cli/KodaClaw.Cli.csproj
PUBLISH_DIR := $(ROOT)/publish

GATEWAY_URL ?= http://127.0.0.1:5076
WORKSPACE_ROOT ?= $(HOME)/.kodaclaw

# Auto-detect current platform RID
UNAME_S := $(shell uname -s 2>/dev/null || echo Windows)
UNAME_M := $(shell uname -m 2>/dev/null || echo x64)
ifeq ($(UNAME_S),Darwin)
  ifeq ($(UNAME_M),arm64)
    CURRENT_RID := osx-arm64
  else
    CURRENT_RID := osx-x64
  endif
else ifeq ($(UNAME_S),Linux)
  ifeq ($(UNAME_M),aarch64)
    CURRENT_RID := linux-arm64
  else
    CURRENT_RID := linux-x64
  endif
else
  CURRENT_RID := win-x64
endif

# ── Registry configuration (override via env or command line) ─────────────────
REGISTRY        ?= registry.cn-hangzhou.aliyuncs.com/vanzheng
IMAGE_NAME      ?= kodaclaw
VERSION         ?= latest
PLATFORMS       ?= linux/amd64,linux/arm64
FULL_IMAGE      := $(REGISTRY)/$(IMAGE_NAME)
BUILDX_BUILDER  ?= kodaclaw-builder

.PHONY: help \
	test-solution test-contract test-integration test-unit \
	web-build web-test web-e2e \
	desktop-test desktop-wave3-smoke desktop-managed-smoke desktop-package-smoke \
	verify-backend verify-web verify-desktop verify-all \
	run-gateway dev-web dev-desktop-attach dev-desktop-managed \
	package-gateway-mac package-gateway-mac-x64 package-gateway-win package-gateway-linux \
	install-kc package-kc-mac package-kc-mac-x64 package-kc-win package-kc-linux \
	build-web-prod \
	package-mac package-mac-x64 package-win package-linux package \
	docker-build docker-up docker-down docker-rebuild docker-logs docker-install \
	docker-login docker-buildx-setup docker-push docker-push-version

help:
	@echo "KodaClaw Makefile targets:"
	@echo "  test-solution          Run dotnet test on KodaClaw.sln with -m:1"
	@echo "  test-contract          Run contract tests"
	@echo "  test-integration       Run integration tests"
	@echo "  test-unit              Run unit tests"
	@echo "  web-build              Build kodaclaw-web"
	@echo "  web-test               Run Vitest for kodaclaw-web"
	@echo "  web-e2e                Run Playwright E2E for kodaclaw-web"
	@echo "  desktop-test           Run desktop test suite"
	@echo "  desktop-wave3-smoke    Run desktop wave3 smoke"
	@echo "  desktop-managed-smoke  Run desktop managed-gateway smoke"
	@echo "  desktop-package-smoke  Run desktop package smoke"
	@echo "  verify-backend         Run solution regression"
	@echo "  verify-web             Run web build + unit + E2E"
	@echo "  verify-desktop         Run desktop test + smoke + package"
	@echo "  verify-all             Run backend + web + desktop verification"
	@echo "  run-gateway            Start local Gateway"
	@echo "  dev-web                Start Vite dev server"
	@echo "  dev-desktop-attach     Start desktop shell in AttachOnly mode"
	@echo "  dev-desktop-managed    Start desktop shell in ManagedChild mode"
	@echo ""
	@echo "kc CLI targets:"
	@echo "  install-kc             Install kc CLI to ~/.kc-cli/bin/ (auto-detects platform: $(CURRENT_RID))"
	@echo "  package-kc-mac         Package kc CLI self-contained (osx-arm64)"
	@echo "  package-kc-mac-x64     Package kc CLI self-contained (osx-x64)"
	@echo "  package-kc-win         Package kc CLI self-contained (win-x64)"
	@echo "  package-kc-linux       Package kc CLI self-contained (linux-x64)"
	@echo ""
	@echo "Packaging targets:"
	@echo "  package-gateway-mac    Publish Gateway self-contained (osx-arm64)"
	@echo "  package-gateway-mac-x64 Publish Gateway self-contained (osx-x64)"
	@echo "  package-gateway-win    Publish Gateway self-contained (win-x64)"
	@echo "  package-gateway-linux  Publish Gateway self-contained (linux-x64)"
	@echo "  build-web-prod         Build kodaclaw-web for production"
	@echo "  package-mac            Full macOS .dmg package (arm64)"
	@echo "  package-mac-x64        Full macOS .dmg package (x64)"
	@echo "  package-win            Full Windows NSIS installer"
	@echo "  package-linux          Full Linux AppImage"
	@echo "  package                Package for current platform (auto-detect)"
	@echo ""
	@echo "Docker targets:"
	@echo "  docker-build           Build Docker image locally (no cache, single-arch)"
	@echo "  docker-up              Start via Docker Compose (detached)"
	@echo "  docker-down            Stop and remove containers"
	@echo "  docker-rebuild         Stop → rebuild → start (full restart)"
	@echo "  docker-logs            Follow container logs"
	@echo "  docker-install         Quick-install using scripts/docker-install.sh"
	@echo "  docker-login           Login to Alibaba Cloud Container Registry"
	@echo "  docker-buildx-setup    Create/ensure multi-arch buildx builder"
	@echo "  docker-push            Push :latest to ACR (multi-arch: $(PLATFORMS))"
	@echo "  docker-push-version    Push VERSION + :latest to ACR (e.g. make docker-push-version VERSION=1.2.3)"
	@echo ""
	@echo "Registry: $(FULL_IMAGE)"
	@echo ""
	@echo "Common overrides:"
	@echo "  GATEWAY_URL=$(GATEWAY_URL)"
	@echo "  WORKSPACE_ROOT=$(WORKSPACE_ROOT)"

docker-build:
	docker build --no-cache -f $(ROOT)/Dockerfile -t kodaclaw:latest $(ROOT)/../..

docker-up:
	docker compose up -d

docker-down:
	docker compose down

docker-rebuild: docker-down docker-build docker-up
	@echo "✓ Container rebuilt and started"

docker-logs:
	docker compose logs -f

docker-install:
	bash scripts/docker-install.sh

# ── Registry: build & push to ACR (Alibaba Cloud) ─────────────────────────────

docker-login:
	docker login registry.cn-hangzhou.aliyuncs.com

docker-buildx-setup:
	@docker buildx inspect $(BUILDX_BUILDER) >/dev/null 2>&1 || \
	  docker buildx create --name $(BUILDX_BUILDER) --driver docker-container --bootstrap
	@echo "✓ buildx builder '$(BUILDX_BUILDER)' ready"

# Push :latest to ACR (multi-arch)
# Usage: make docker-push
docker-push: docker-buildx-setup
	docker buildx build \
	  --builder $(BUILDX_BUILDER) \
	  --platform $(PLATFORMS) \
	  --file $(ROOT)/Dockerfile \
	  --tag $(FULL_IMAGE):latest \
	  --push \
	  $(ROOT)/../..
	@echo "✓ Pushed $(FULL_IMAGE):latest"

# Push versioned tag + :latest to ACR (multi-arch)
# Usage: make docker-push-version VERSION=1.2.3
docker-push-version: docker-buildx-setup
	@[ -n "$(VERSION)" ] || (echo "Error: VERSION is required. Usage: make docker-push-version VERSION=1.2.3"; exit 1)
	docker buildx build \
	  --builder $(BUILDX_BUILDER) \
	  --platform $(PLATFORMS) \
	  --file $(ROOT)/Dockerfile \
	  --tag $(FULL_IMAGE):$(VERSION) \
	  --tag $(FULL_IMAGE):latest \
	  --push \
	  $(ROOT)/../..
	@echo "✓ Pushed $(FULL_IMAGE):$(VERSION) and $(FULL_IMAGE):latest"

test-solution:
	dotnet test "$(SOLUTION)" -m:1

test-contract:
	dotnet test "$(ROOT)/tests/KodaClaw.ContractTests/KodaClaw.ContractTests.csproj"

test-integration:
	dotnet test "$(ROOT)/tests/KodaClaw.IntegrationTests/KodaClaw.IntegrationTests.csproj"

test-unit:
	dotnet test "$(ROOT)/tests/KodaClaw.UnitTests/KodaClaw.UnitTests.csproj"

web-build:
	cd "$(WEB_DIR)" && npm run build

web-test:
	cd "$(WEB_DIR)" && npm run test

web-e2e:
	cd "$(WEB_DIR)" && npm run test:e2e

desktop-test:
	cd "$(DESKTOP_DIR)" && npm run test

desktop-wave3-smoke:
	cd "$(DESKTOP_DIR)" && npm run smoke:wave3

desktop-managed-smoke:
	cd "$(DESKTOP_DIR)" && npm run smoke:managed-gateway

desktop-package-smoke:
	cd "$(DESKTOP_DIR)" && npm run package:smoke

verify-backend: test-solution

verify-web:
	cd "$(WEB_DIR)" && npm run build && npm run test && npm run test:e2e

verify-desktop:
	cd "$(DESKTOP_DIR)" && npm run test && npm run smoke:wave3 && npm run smoke:managed-gateway && npm run package:smoke

verify-all: verify-backend verify-web verify-desktop

run-gateway:
	KODACLAW_WORKSPACE_ROOT="$(WORKSPACE_ROOT)" \
	KODACLAW_GATEWAY_URL="$(GATEWAY_URL)" \
	dotnet run --project "$(GATEWAY_PROJECT)" --urls "$(GATEWAY_URL)"

dev-web:
	cd "$(WEB_DIR)" && npm run dev

dev-desktop-attach:
	cd "$(DESKTOP_DIR)" && \
	KODACLAW_DESKTOP_GATEWAY_URL="$(GATEWAY_URL)" \
	KODACLAW_DESKTOP_GATEWAY_LIFECYCLE_MODE=AttachOnly \
	npm run dev

dev-desktop-managed:
	cd "$(DESKTOP_DIR)" && \
	KODACLAW_DESKTOP_GATEWAY_URL="$(GATEWAY_URL)" \
	KODACLAW_DESKTOP_GATEWAY_LIFECYCLE_MODE=ManagedChild \
	npm run dev

# ── Packaging ──────────────────────────────────────────────────────────────

install-kc:
	dotnet publish "$(CLI_PROJECT)" \
	  -r $(CURRENT_RID) --no-self-contained \
	  -p:PublishSingleFile=true \
	  -c Release \
	  -o "$(HOME)/.kc-cli/bin"

package-kc-mac:
	dotnet publish "$(CLI_PROJECT)" \
	  -r osx-arm64 --self-contained \
	  -p:PublishSingleFile=true \
	  -c Release \
	  -o "$(PUBLISH_DIR)/kc-mac"

package-kc-mac-x64:
	dotnet publish "$(CLI_PROJECT)" \
	  -r osx-x64 --self-contained \
	  -p:PublishSingleFile=true \
	  -c Release \
	  -o "$(PUBLISH_DIR)/kc-mac-x64"

package-kc-win:
	dotnet publish "$(CLI_PROJECT)" \
	  -r win-x64 --self-contained \
	  -p:PublishSingleFile=true \
	  -c Release \
	  -o "$(PUBLISH_DIR)/kc-win"

package-kc-linux:
	dotnet publish "$(CLI_PROJECT)" \
	  -r linux-x64 --self-contained \
	  -p:PublishSingleFile=true \
	  -c Release \
	  -o "$(PUBLISH_DIR)/kc-linux"

package-gateway-mac:
	dotnet publish "$(GATEWAY_PROJECT)" \
	  -r osx-arm64 --self-contained \
	  -p:PublishSingleFile=true \
	  -p:IncludeNativeLibrariesForSelfExtract=true \
	  -p:DebugType=None \
	  -p:DebugSymbols=false \
	  -c Release \
	  -o "$(PUBLISH_DIR)/gateway-mac"

package-gateway-mac-x64:
	dotnet publish "$(GATEWAY_PROJECT)" \
	  -r osx-x64 --self-contained \
	  -p:PublishSingleFile=true \
	  -p:IncludeNativeLibrariesForSelfExtract=true \
	  -p:DebugType=None \
	  -p:DebugSymbols=false \
	  -c Release \
	  -o "$(PUBLISH_DIR)/gateway-mac-x64"

package-gateway-win:
	dotnet publish "$(GATEWAY_PROJECT)" \
	  -r win-x64 --self-contained \
	  -p:PublishSingleFile=true \
	  -p:IncludeNativeLibrariesForSelfExtract=true \
	  -p:DebugType=None \
	  -p:DebugSymbols=false \
	  -c Release \
	  -o "$(PUBLISH_DIR)/gateway-win"

package-gateway-linux:
	dotnet publish "$(GATEWAY_PROJECT)" \
	  -r linux-x64 --self-contained \
	  -p:PublishSingleFile=true \
	  -p:IncludeNativeLibrariesForSelfExtract=true \
	  -p:DebugType=None \
	  -p:DebugSymbols=false \
	  -c Release \
	  -o "$(PUBLISH_DIR)/gateway-linux"

build-web-prod:
	cd "$(WEB_DIR)" && npm run build

package-mac: package-gateway-mac build-web-prod
	cd "$(DESKTOP_DIR)" && \
	npm run build && \
	CSC_IDENTITY_AUTO_DISCOVERY=false \
	npx electron-builder \
	  --config electron-builder.release.json \
	  --mac --arm64 \
	  --publish never

package-mac-x64: package-gateway-mac-x64 build-web-prod
	cd "$(DESKTOP_DIR)" && \
	npm run build && \
	CSC_IDENTITY_AUTO_DISCOVERY=false \
	npx electron-builder \
	  --config electron-builder.release.json \
	  --mac --x64 \
	  --publish never

package-win: package-gateway-win build-web-prod
	cd "$(DESKTOP_DIR)" && \
	npm run build && \
	npx electron-builder \
	  --config electron-builder.release.json \
	  --win \
	  --publish never

package-linux: package-gateway-linux build-web-prod
	cd "$(DESKTOP_DIR)" && \
	npm run build && \
	npx electron-builder \
	  --config electron-builder.release.json \
	  --linux \
	  --publish never

# Auto-detect current platform
UNAME_S := $(shell uname -s)
UNAME_M := $(shell uname -m)

ifeq ($(UNAME_S), Darwin)
  ifeq ($(UNAME_M), arm64)
    package: package-mac
  else
    package: package-mac-x64
  endif
else ifeq ($(UNAME_S), Linux)
  package: package-linux
else
  package: package-win
endif
