# Copilot runtime for offline builds

Building MarTechChat downloads the Copilot runtime (about 90 MB) from github.com. If your network blocks that download,
the build stops with an error like:

    error MSB3923: Failed to download file "https://github.com/github/copilot-cli/releases/download/v1.0.107/SHA256SUMS.txt"

Download it in your browser instead and put it in this folder:

1. Open https://github.com/github/copilot-cli/releases/tag/v1.0.107
2. Under **Assets**, download `github-copilot-1.0.107-win32-x64.tgz` (not the `.zip`).
3. Move it into this folder, next to this file. Don't unpack it.
4. Build or publish again. The build prints
   "Using the Copilot runtime from ...github-copilot-1.0.107-win32-x64.tgz instead of downloading it."

The build checks the file against the checksum GitHub publishes, and stops if it doesn't match. The file is ignored by
git, so it isn't committed. The version (1.0.107) is the one the Copilot SDK package in `src/Hark.Core` asks for; if
that package is updated, the build error names the new version to download.
