# Set up the extension's translator dependency (run once after cloning)
setup:
    git submodule update --init --recursive
    cd extension/vendor/jabref-browser-extension && python scripts/import_and_patch_translators.py

# Build and install the hub
install:
    powershell -ExecutionPolicy Bypass -File install/install.ps1

# Run hub tests
test:
    cd hub && dotnet test
