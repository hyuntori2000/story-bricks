# Dashboard workspace

This folder belongs to the design team. Add your own dashboard here.

The working capture monitor remains available at `http://127.0.0.1:8769/` when the recognition server runs. Its read-only `GET /latest.json` endpoint exposes the latest capture and recognition result. Do not change capture or recognition code to build a new dashboard.

For optional event-driven media, implement the manifest contract in `Documentation/COMPONENTS.md` and enable `UseDashboardCatalog` on the presenter's designer-owned prefab. Remote catalog loading is off by default. This handoff does not impose a dashboard framework or UX.
