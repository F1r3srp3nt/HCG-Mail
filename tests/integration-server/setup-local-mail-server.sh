#!/usr/bin/env bash
# Local IMAP/SMTP test server for HCG Mail end-to-end tests (Linux, run as root in a
# disposable container or VM — it installs a test CA into the system trust store).
#
#   Dovecot 2.3  IMAP  993 (SSL/TLS) and 143 (STARTTLS), SPECIAL-USE folders, PLAIN/LOGIN over TLS only
#   Postfix      SMTP  465 (SSL/TLS) and 587 (STARTTLS required), SASL auth via Dovecot
#   Delivery     local only (hcgmail.test); every other destination is rejected, nothing leaves the machine
#   TLS          certificate for mail.hcgmail.test signed by a throwaway CA, with a CRL served on
#                http://127.0.0.1:8099 so certificate revocation checking is exercised for real
#
# Usage:  sudo ./setup-local-mail-server.sh [start|stop|cleanup]
# Users:  ola@hcgmail.test / ola-test-password      kari@hcgmail.test / kari-test-password
set -euo pipefail

ROOT=/srv/hcgmail-test
HOST=mail.hcgmail.test
CA_NAME=hcg-mail-test-ca

stop() {
  dovecot -c "$ROOT/dovecot.conf" stop 2>/dev/null || true
  postfix stop 2>/dev/null || true
  [ -f "$ROOT/crl-http.pid" ] && kill "$(cat "$ROOT/crl-http.pid")" 2>/dev/null || true
  rm -f "$ROOT/crl-http.pid"
  # Also stop CRL servers left behind by interrupted runs.
  ps -eo pid=,args= | awk '$2 ~ /python3$/ && $3 == "-m" && $4 == "http.server" && $5 == "8099" {print $1}' | xargs -r kill 2>/dev/null || true
}

cleanup() {
  stop
  rm -f "/usr/local/share/ca-certificates/$CA_NAME.crt"
  update-ca-certificates --fresh >/dev/null
  sed -i "/$HOST/d" /etc/hosts
  rm -rf "$ROOT"
  echo "Removed test server, test CA and hosts entry."
}

case "${1:-start}" in
  stop) stop; exit 0 ;;
  cleanup) cleanup; exit 0 ;;
esac

command -v dovecot >/dev/null || { echo "Install dovecot-imapd dovecot-lmtpd postfix first"; exit 1; }
stop
mkdir -p "$ROOT"/{pki,mail,crl}
cd "$ROOT/pki"

# ---- PKI: CA, server certificate with CRL distribution point, CRL ----
if [ ! -f ca.key ]; then
  openssl req -x509 -newkey rsa:2048 -nodes -days 30 -subj "/CN=HCG Mail Test CA (local, disposable)" \
    -keyout ca.key -out ca.crt -addext "basicConstraints=critical,CA:TRUE" -addext "keyUsage=critical,keyCertSign,cRLSign" 2>/dev/null
  cat > ca.cnf <<CNF
[ ca ]
default_ca = test_ca
[ test_ca ]
dir = $ROOT/pki
database = \$dir/index.txt
new_certs_dir = \$dir
certificate = \$dir/ca.crt
private_key = \$dir/ca.key
serial = \$dir/serial
crlnumber = \$dir/crlnumber
default_md = sha256
default_days = 30
default_crl_days = 30
policy = any
copy_extensions = copy
[ any ]
commonName = supplied
CNF
  : > index.txt; echo 1000 > serial; echo 1000 > crlnumber
  openssl req -newkey rsa:2048 -nodes -subj "/CN=$HOST" -keyout server.key -out server.csr \
    -addext "subjectAltName=DNS:$HOST" \
    -addext "crlDistributionPoints=URI:http://127.0.0.1:8099/test-ca.crl" \
    -addext "extendedKeyUsage=serverAuth" 2>/dev/null
  openssl ca -batch -config ca.cnf -in server.csr -out server.crt -notext 2>/dev/null
  openssl ca -config ca.cnf -gencrl -out ../crl/test-ca.crl.pem 2>/dev/null
  openssl crl -in ../crl/test-ca.crl.pem -outform DER -out ../crl/test-ca.crl
fi
cp ca.crt "/usr/local/share/ca-certificates/$CA_NAME.crt"
update-ca-certificates >/dev/null
grep -q "$HOST" /etc/hosts || echo "127.0.0.1 $HOST" >> /etc/hosts

# CRL endpoint (plain HTTP on loopback, as CRLs normally are)
(cd "$ROOT/crl" && nohup python3 -m http.server 8099 --bind 127.0.0.1 </dev/null >/dev/null 2>&1 & echo $! > "$ROOT/crl-http.pid")

# ---- Users ----
id vmail >/dev/null 2>&1 || useradd -r -d "$ROOT/mail" -s /usr/sbin/nologin vmail
cat > "$ROOT/users" <<USERS
ola@hcgmail.test:{PLAIN}ola-test-password
kari@hcgmail.test:{PLAIN}kari-test-password
USERS
chown -R vmail:vmail "$ROOT/mail"

# ---- Dovecot ----
cat > "$ROOT/dovecot.conf" <<CONF
protocols = imap lmtp
listen = 127.0.0.1
base_dir = $ROOT/run
log_path = $ROOT/dovecot.log
ssl = required
ssl_cert = <$ROOT/pki/server.crt
ssl_key = <$ROOT/pki/server.key
ssl_min_protocol = TLSv1.2
disable_plaintext_auth = yes
auth_mechanisms = plain login
mail_location = maildir:$ROOT/mail/%u/Maildir
mail_uid = vmail
mail_gid = vmail
first_valid_uid = 1
passdb {
  driver = passwd-file
  args = scheme=PLAIN username_format=%u $ROOT/users
}
userdb {
  driver = static
  args = uid=vmail gid=vmail home=$ROOT/mail/%u
}
namespace inbox {
  inbox = yes
  separator = /
  mailbox Drafts {
    special_use = \Drafts
    auto = subscribe
  }
  mailbox Sent {
    special_use = \Sent
    auto = subscribe
  }
  mailbox Junk {
    special_use = \Junk
    auto = subscribe
  }
  mailbox Trash {
    special_use = \Trash
    auto = subscribe
  }
  mailbox Archive {
    special_use = \Archive
    auto = subscribe
  }
}
service imap-login {
  inet_listener imap {
    port = 143
  }
  inet_listener imaps {
    port = 993
    ssl = yes
  }
}
service lmtp {
  unix_listener /var/spool/postfix/private/dovecot-lmtp {
    mode = 0600
    user = postfix
    group = postfix
  }
}
service auth {
  unix_listener /var/spool/postfix/private/auth {
    mode = 0660
    user = postfix
    group = postfix
  }
}
CONF
mkdir -p "$ROOT/run"

# ---- Postfix ----
if [ ! -s /etc/postfix/main.cf ] || grep -q '^setgid_group = *$' /etc/postfix/main.cf; then
  cat > /etc/postfix/main.cf <<MAIN
queue_directory = /var/spool/postfix
command_directory = /usr/sbin
daemon_directory = /usr/lib/postfix/sbin
data_directory = /var/lib/postfix
mail_owner = postfix
setgid_group = postdrop
MAIN
fi
postconf -e \
  "myhostname = $HOST" "mydomain = hcgmail.test" "mydestination =" "inet_interfaces = loopback-only" \
  "inet_protocols = ipv4" "compatibility_level = 3.6" "maillog_file = /var/log/hcgmail-test-postfix.log" \
  "virtual_mailbox_domains = hcgmail.test" "virtual_transport = lmtp:unix:private/dovecot-lmtp" \
  "default_transport = error:outbound delivery is disabled on the HCG Mail test server" \
  "relay_transport = error:outbound delivery is disabled on the HCG Mail test server" \
  "smtpd_tls_cert_file = $ROOT/pki/server.crt" "smtpd_tls_key_file = $ROOT/pki/server.key" \
  "smtpd_tls_security_level = may" "smtpd_tls_auth_only = yes" "smtpd_tls_mandatory_protocols = >=TLSv1.2" \
  "smtpd_sasl_type = dovecot" "smtpd_sasl_path = private/auth" \
  "smtpd_relay_restrictions = permit_sasl_authenticated, reject_unauth_destination"
postconf -M "smtps/inet=smtps inet n - y - - smtpd"
postconf -P "smtps/inet/smtpd_tls_wrappermode=yes" "smtps/inet/smtpd_sasl_auth_enable=yes" \
  "smtps/inet/smtpd_client_restrictions=permit_sasl_authenticated,reject"
postconf -M "submission/inet=submission inet n - y - - smtpd"
postconf -P "submission/inet/smtpd_tls_security_level=encrypt" "submission/inet/smtpd_sasl_auth_enable=yes" \
  "submission/inet/smtpd_client_restrictions=permit_sasl_authenticated,reject"

dovecot -c "$ROOT/dovecot.conf" </dev/null >/dev/null 2>&1
postfix start </dev/null >/dev/null 2>&1 || postfix reload </dev/null >/dev/null 2>&1
sleep 1
echo "Test server ready: IMAP $HOST:993 (SSL/TLS) / 143 (STARTTLS), SMTP $HOST:465 (SSL/TLS) / 587 (STARTTLS)"
