# Security

## Reporting a vulnerability

Please report security problems privately, not in a public issue. On the [Clasp-releases repository](https://github.com/yirock7/Clasp-releases), open the **Security** tab and choose **Report a vulnerability** (GitHub's private vulnerability reporting), or use [this direct link](https://github.com/yirock7/Clasp-releases/security/advisories/new). Only the maintainer can see what you send.

Please include what you found, the Clasp version, and the steps to reproduce it. A proof of concept helps but isn't required.

## What counts

The parts of Clasp that matter most are:

- **The app**, including how it handles Bluetooth broadcasts from nearby devices, which anyone can fake.
- **The updater**: how it downloads an installer and checks its signature before running it.
- **The installer**: what it writes, and where.
- **The signature check** used for manual installs (`windows/hub/tools/Verify-ClaspInstaller.ps1`).

Problems in Windows itself, in GitHub, or in Apple's products are outside what I can fix, though I would still like to hear if Clasp makes one worse.

## What to expect

Clasp is a hobby project, and I'm one person working in spare time, so I can't promise a fixed response time. I aim to acknowledge a report within about a week, and to ship a fix for anything serious within a few weeks, but it may take longer and I will tell you if it does. I will keep you posted, and credit you in the release notes if you'd like. Please give me reasonable time to ship a fix before sharing details publicly.

There is no bug bounty, and Clasp comes with no warranty (see the [licence](LICENSE)), but I take reports seriously and I am grateful for them.

## Supported versions

Only the latest release gets fixes. Clasp can update itself, so most people are on it already.
