"""Run the hand-region receiver with its own local camera monitor."""
from pathlib import Path

import camera_frame_server


def main():
    camera_frame_server.DASHBOARD = Path(__file__).with_name("table_depth_dashboard.html").read_bytes()
    camera_frame_server.main()


if __name__ == "__main__":
    main()
