"""
NT8 ATI Bridge
==============
Submits orders to NinjaTrader 8 via the built-in Automated Trading Interface
(ATI) TCP socket. No CSV files, no polling, no add-ons required.

NT8 setup (one-time):
  Tools → Options → Automated Trading Interface
    ✓ AT Interface enabled
    Port: 36973 (default)

Usage:
    from nt_rest_bridge import NTBridge, NTBridgeError

    nt = NTBridge(account="Sim101")
    nt.market_order("NQ 06-26", "BUY",  1)
    nt.market_order("NQ 06-26", "SELL", 1)
    nt.bracket_order("NQ 06-26", "LONG", qty=1, stop_loss=18200.0, take_profit=18300.0)
    nt.close_position("NQ 06-26")
    pos = nt.get_position("NQ 06-26")   # "long" / "short" / "flat"
"""

import socket
from typing import Literal

_HOST = "localhost"
_PORT = 36973


class NTBridgeError(Exception):
    pass


class NTBridge:
    def __init__(
        self,
        account: str,
        host: str = _HOST,
        port: int = _PORT,
        timeout: float = 5.0,
    ):
        self.account = account
        self.host    = host
        self.port    = port
        self.timeout = timeout

    # ------------------------------------------------------------------
    # Orders
    # ------------------------------------------------------------------

    def market_order(
        self,
        instrument: str,
        action: Literal["BUY", "SELL"],
        quantity: int,
        tif: str = "DAY",
    ) -> str:
        return self._command(
            f"PLACE;Account={self.account};Instrument={instrument};"
            f"Action={action};Qty={quantity};OrderType=MARKET;TIF={tif}"
        )

    def limit_order(
        self,
        instrument: str,
        action: Literal["BUY", "SELL"],
        quantity: int,
        limit_price: float,
        tif: str = "DAY",
    ) -> str:
        return self._command(
            f"PLACE;Account={self.account};Instrument={instrument};"
            f"Action={action};Qty={quantity};OrderType=LIMIT;"
            f"LimitPrice={limit_price};TIF={tif}"
        )

    def stop_market_order(
        self,
        instrument: str,
        action: Literal["BUY", "SELL"],
        quantity: int,
        stop_price: float,
        tif: str = "DAY",
    ) -> str:
        return self._command(
            f"PLACE;Account={self.account};Instrument={instrument};"
            f"Action={action};Qty={quantity};OrderType=STOPMARKET;"
            f"StopPrice={stop_price};TIF={tif}"
        )

    def cancel_order(self, order_id: str) -> str:
        return self._command(f"CANCEL;OrderId={order_id}")

    def cancel_all_orders(self, instrument: str) -> str:
        return self._command(
            f"CANCELALLORDERS;Account={self.account};Instrument={instrument}"
        )

    def close_position(self, instrument: str) -> str:
        """Flatten position and cancel all working orders for this instrument."""
        return self._command(
            f"CLOSEPOSITION;Account={self.account};Instrument={instrument}"
        )

    def flatten_everything(self) -> str:
        """Flatten ALL positions and cancel ALL orders on the account."""
        return self._command(f"FLATTENEVERYTHING;Account={self.account}")

    # ------------------------------------------------------------------
    # Convenience: entry + bracket
    # ------------------------------------------------------------------

    def bracket_order(
        self,
        instrument: str,
        direction: Literal["LONG", "SHORT", "BUY", "SELL"],
        quantity: int,
        stop_loss: float,
        take_profit: float,
    ) -> dict:
        """
        Submit a market entry followed immediately by a SL stop and TP limit.
        Returns dict with 'entry', 'sl', 'tp' ATI responses.

        NT8 will cancel the orphaned leg when one fills, provided the
        claudetrader.cs / fvgbot.cs strategy is running on the chart with
        OCO cancellation enabled — OR use an ATM strategy name instead.
        """
        action = "BUY" if direction in ("LONG", "BUY") else "SELL"
        exit_action = "SELL" if action == "BUY" else "BUY"

        entry = self.market_order(instrument, action, quantity, tif="DAY")

        sl = self._command(
            f"PLACE;Account={self.account};Instrument={instrument};"
            f"Action={exit_action};Qty={quantity};OrderType=STOPMARKET;"
            f"StopPrice={stop_loss};TIF=GTC"
        )

        tp = self._command(
            f"PLACE;Account={self.account};Instrument={instrument};"
            f"Action={exit_action};Qty={quantity};OrderType=LIMIT;"
            f"LimitPrice={take_profit};TIF=GTC"
        )

        return {"entry": entry, "sl": sl, "tp": tp}

    # ------------------------------------------------------------------
    # Position / account queries (via NtDirect.dll if available,
    # otherwise ATI socket returns position state)
    # ------------------------------------------------------------------

    def get_position(self, instrument: str) -> str:
        """
        Returns the raw ATI response string, e.g. 'long', 'short', or 'flat'.
        Requires NtDirect.dll for richer data — see nt_direct_queries.py.
        """
        return self._command(
            f"MARKETPOSITION;Account={self.account};Instrument={instrument}"
        )

    # ------------------------------------------------------------------
    # Internal
    # ------------------------------------------------------------------

    def _command(self, cmd: str) -> str:
        """Send a single ATI command, return the response string."""
        msg = (cmd + "\r\n").encode("ascii")
        try:
            with socket.create_connection((self.host, self.port), timeout=self.timeout) as s:
                s.sendall(msg)
                # ATI sends a short status response then closes the connection
                chunks = []
                while True:
                    chunk = s.recv(4096)
                    if not chunk:
                        break
                    chunks.append(chunk)
                response = b"".join(chunks).decode("ascii").strip()
                return response
        except ConnectionRefusedError:
            raise NTBridgeError(
                f"Cannot connect to NT8 ATI on {self.host}:{self.port}. "
                "Is NinjaTrader running with ATI enabled? "
                "(Tools → Options → Automated Trading Interface)"
            )
        except OSError as e:
            raise NTBridgeError(f"NT8 ATI socket error: {e}") from e


# ------------------------------------------------------------------
# Smoke-test  (python nt_rest_bridge.py)
# ------------------------------------------------------------------
if __name__ == "__main__":
    nt = NTBridge(account="Sim101")

    print("Testing connection to NT8 ATI...")
    try:
        response = nt.get_position("NQ 06-26")
        print(f"Position response: {response!r}")
        print("Connection OK.")
    except NTBridgeError as e:
        print(f"FAILED: {e}")
        print()
        print("Steps to enable ATI in NinjaTrader 8:")
        print("  1. Tools → Options → Automated Trading Interface")
        print("  2. Check 'AT Interface enabled'")
        print("  3. Port should be 36973")
        print("  4. Click OK and restart NinjaTrader if prompted")
