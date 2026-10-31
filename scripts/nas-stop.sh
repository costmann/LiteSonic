#!/bin/sh
# Ferma LiteSonic
cd "$(dirname "$0")" || exit 1
if [ -f litesonic.pid ] && kill "$(cat litesonic.pid)" 2>/dev/null; then echo "LiteSonic fermato"; else echo "LiteSonic non era in esecuzione"; fi
rm -f litesonic.pid
