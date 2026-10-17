#!/usr/bin/env bash
# A throwaway "Apple Development" identity and a development provisioning profile for any app
# ID of the team, made with openssl, for exercising device signing in CI. Self-signed, so
# Apple's codesign verifies the signature ("valid on disk") but not the chain; nothing signed
# with it installs on a real device.
#
#   test/ios/make-test-signing.sh <out-dir>   ->  dev.p12, dev.p12.password, test.mobileprovision
set -euo pipefail
out=$1; mkdir -p "$out"; cd "$out"
team=ABCDE12345
cat > ext.cnf <<CNF
[req]
distinguished_name = dn
[dn]
[v3]
basicConstraints = critical, CA:false
keyUsage = critical, digitalSignature
extendedKeyUsage = critical, codeSigning
CNF
# MSYS_NO_PATHCONV: keep Git Bash on Windows from rewriting the /UID=... subject as a path.
MSYS_NO_PATHCONV=1 openssl req -x509 -newkey rsa:2048 -nodes -keyout key.pem -out cert.pem -days 30 \
  -config ext.cnf -extensions v3 -subj "/UID=$team/CN=Apple Development: AotAnywhere CI ($team)/OU=$team/O=AotAnywhere CI/C=CA"
printf 'ci-password\n' > dev.p12.password
openssl pkcs12 -export -inkey key.pem -in cert.pem -out dev.p12 -passout pass:ci-password
cert=$(openssl x509 -in cert.pem -outform der | base64 | tr -d '\r\n')
now=$(date -u +%Y-%m-%dT%H:%M:%SZ)
cat > profile.plist <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
	<key>ApplicationIdentifierPrefix</key><array><string>$team</string></array>
	<key>CreationDate</key><date>$now</date>
	<key>DeveloperCertificates</key><array><data>$cert</data></array>
	<key>Entitlements</key>
	<dict>
		<key>application-identifier</key><string>$team.*</string>
		<key>com.apple.developer.team-identifier</key><string>$team</string>
		<key>get-task-allow</key><true/>
		<key>keychain-access-groups</key><array><string>$team.*</string></array>
	</dict>
	<key>ExpirationDate</key><date>2099-01-01T00:00:00Z</date>
	<key>Name</key><string>AotAnywhere CI Development</string>
	<key>Platform</key><array><string>iOS</string></array>
	<key>ProvisionedDevices</key><array><string>00008110-000000000000001E</string></array>
	<key>TeamIdentifier</key><array><string>$team</string></array>
	<key>TeamName</key><string>AotAnywhere CI</string>
	<key>UUID</key><string>5A0C1E2D-3F4B-4C5D-8E6F-7A8B9C0D1E2F</string>
	<key>Version</key><integer>1</integer>
</dict>
</plist>
PLIST
openssl smime -sign -nodetach -binary -in profile.plist -signer cert.pem -inkey key.pem -outform der -out test.mobileprovision
rm -f key.pem ext.cnf profile.plist
