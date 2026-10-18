import socket
import time

# Accepts connections and never answers, so a page from it never starts loading.
s = socket.socket()
s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
s.bind(('127.0.0.1', 18765))
s.listen(16)
held = []
end = time.time() + 60
s.settimeout(1)
while time.time() < end:
    try:
        c, _ = s.accept()
        held.append(c)
    except socket.timeout:
        pass
