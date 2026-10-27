"""Create an access token for one user. Prints the token once (give it to the user)
and the JSON line to merge into the tokens secret. Only the SHA-256 is stored."""
import hashlib
import json
import re
import secrets
import sys


def main() -> None:
    if len(sys.argv) != 2 or not re.fullmatch(r"[a-z0-9_-]{1,32}", sys.argv[1]):
        sys.exit("usage: python -m mfa_service.admin USER_ID   (a-z 0-9 _ -)")
    token = "sim_" + secrets.token_urlsafe(32)
    print("TOKEN (shown once, paste into Subtitle Edit):", token, file=sys.stderr)
    print(json.dumps({hashlib.sha256(token.encode()).hexdigest(): sys.argv[1]}))


if __name__ == "__main__":
    main()

