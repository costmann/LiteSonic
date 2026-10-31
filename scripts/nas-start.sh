#!/bin/sh
# Avvia LiteSonic in background (usato anche dall Utilita di pianificazione di DSM all avvio)
cd "$(dirname "$0")" || exit 1
if [ -f litesonic.pid ] && kill -0 "$(cat litesonic.pid)" 2>/dev/null; then
    echo "LiteSonic e gia in esecuzione (PID $(cat litesonic.pid))"; exit 0
fi
# rotazione semplice del log oltre i 5 MB
if [ -f litesonic.log ] && [ "$(wc -c < litesonic.log)" -gt 5000000 ]; then mv litesonic.log litesonic.log.1; fi
nohup ./litesonic >> litesonic.log 2>&1 < /dev/null &
echo $! > litesonic.pid
echo "LiteSonic avviato (PID $!)"
