# Changelog

## Unreleased

- **RTT safety gate**: Continuous measurement and display of round-trip time to the ROS session. During an active control session, if latency exceeds the allowed threshold (default 200 ms), control is stopped automatically while the video stream and robot connection remain until the session ends.
- **Control start UX**: After connecting to the robot and receiving system state, the operator first sees a confirmation screen with robot status, then explicitly confirms readiness before control begins.
- **Control gain/loss events**: Handling for receiving and losing control, with matching UI and application logic flows.
- **Connection architecture**: The app no longer connects only directly to the robot; it works through an intermediate service that assigns tasks, proxies ROS traffic, and manages the teleop session.
- **Teleoperator authentication (REST)**: Login and password via REST API — sign-in, access token retrieval, session storage, and sign-out.
- **Task cards via REST**: Help requests are loaded from the server instead of a ROS topic, converted to local task cards, and shown in the UI.
- **Accept task via REST**: Confirming a task yields a session id, then connects to the corresponding teleop session.
- **Dataset metadata**: Exported data includes time the task was accepted for work, plus a log of control gain and loss events.
- **Network robustness**: RTT measurement runs asynchronously so the main thread is not blocked, avoiding UI freezes on poor connections.
