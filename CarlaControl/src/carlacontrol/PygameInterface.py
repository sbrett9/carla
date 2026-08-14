"""Pygame-based camera viewer interface for CARLA.

Provides a reusable pygame window with:
- Image rendering from CARLA camera sensors
- Unreal-editor-style flying controls (RMB + WASD/EQ)
- Compass, HUD, and overlay rendering
- Depth-based world picking (Ctrl+LMB)
- Configurable hotkeys and callbacks
"""

import logging
import math
import time
from collections.abc import Callable
from typing import Any

import numpy as np
import pygame

from .Pose import Pose


class PygameInterface:
    def __init__(
        self,
        args,
        world=None,
        window_title: str = "CARLA Viewer",
        font_name: str = "consolas",
        font_size: int = 16,
        sync: bool = False,
        sensors=None,
        controller=None,
        traffic=None,
        telemetry=None,
        recorder=None,
        scenario=None,
        orbit_sensor_controller=None,
    ):
        """Initialize pygame interface.

        Args:
            args: Parsed arguments with width, height, fixed_delta, time_rate attributes
            world: CARLA world object for layer/state control (optional)
            window_title: Title for the pygame window
            font_name: Font name for text rendering
            font_size: Font size in points
            sync: Whether running in synchronous mode (affects target FPS)
            sensors: Optional SensorRig instance
            controller: Optional PyGameSensorController instance
            traffic: Optional TrafficController instance
            telemetry: Optional TelemetryController instance
            recorder: Optional NativeRecorder instance
            scenario: Optional ScenarioController instance
            orbit_sensor_controller: Optional OrbitSensorController instance
        """
        self.width = args.width
        self.height = args.height
        self.target_fps = (1.0 / args.fixed_delta) if sync else 60.0
        self.world = world
        self.solar_poll_frame = 0
        self.sync = sync
        self.time_rate = args.time_rate

        self.sensors = sensors
        self.controller = controller
        self.traffic = traffic
        self.telemetry = telemetry
        self.recorder = recorder
        self.scenario = scenario
        self.orbit_sensor_controller = orbit_sensor_controller
        self.logger = logging.getLogger(__name__)

        # Pygame Setup, and pull in class attributes for quick access
        pygame.init()
        pygame.font.init()

        self.font = pygame.font.SysFont(font_name, font_size)
        self.display = pygame.display.set_mode((args.width, args.height))
        pygame.display.set_caption(window_title)
        self.clock = pygame.time.Clock()

        self.running = True
        self.looking = False
        self.mouse_sensitivity = 0.15

        self.note = None  # (message, timestamp)
        self.pick_result = None
        self.pick_close_reigon = None

        # set up rendering things
        self._perimeter_corners: list[tuple[float, float, float]] | None = None
        self._margin_corners: list[tuple[float, float, float]] | None = None
        self._proj_focal_length: float | None = None
        self._proj_cx: float | None = None
        self._proj_cy: float | None = None

        # setup flags for world control
        self._flags: dict[str, bool] = {
            "show_perimeter": False,
            "show_margin": False,
            "photoreal_visible": True,
            "ground_visible": False,
            "ground_collision": True,
            "road_rendered": True,
            "signals_visible": True,
            "time_advancing": False,
        }

        # set up hotkeys for functionality
        self._hotkey_callbacks: dict[int, Callable[[], None]] = {}

        # register built-in hotkeys and subsystem callbacks
        self._register_builtin_hotkeys()
        self._register_subsystem_hotkeys()

        if hasattr(args, "time_advance"):
            self._flags["time_advancing"] = bool(args.time_advance)

        if hasattr(args, "fov"):
            self.setup_boundary_overlays(fov=args.fov)
        
        self.logger.info(
            f"pygame interface initialized: {self.width}x{self.height}, "
            f"target_fps={self.target_fps:.1f}, sync={self.sync}"
        )

    def register_hotkey(self, key: int, callback: Callable[[], None]) -> None:
        """Register a callback for a pygame key constant.

        Args:
            key: pygame key constant (e.g., pygame.K_t)
            callback: Function to call when key is pressed
        """
        self._hotkey_callbacks[key] = callback

    def get_flag(self, name: str, default: bool = False) -> bool:
        """Get a boolean flag value.

        Args:
            name: Flag name
            default: Default value if flag not set

        Returns:
            Flag value or default
        """
        return self._flags.get(name, default)

    def toggle_flag(self, name: str, default: bool = False) -> bool:
        """Toggle a boolean flag and return the new value.

        Args:
            name: Flag name
            default: Initial value if flag not set

        Returns:
            New flag value after toggle
        """
        current = self._flags.get(name, default)
        new_value = not current
        self._flags[name] = new_value
        return new_value

    def setup_boundary_overlays(self, fov: float) -> None:
        """Initialize perimeter and margin boundary overlays from staging bounds.

        Fetches staging bounds from the world, computes corner positions with ground Z,
        and stores projection parameters for rendering.

        Args:
            fov: Camera field of view in degrees
        """
        if self.world is None:
            return

        self._proj_focal_length = self.width / (2.0 * math.tan(math.radians(fov) / 2.0))
        self._proj_cx = self.width / 2.0
        self._proj_cy = self.height / 2.0

        try:
            bounds = self.world.get_staging_bounds()
        except Exception:
            bounds = None

        if bounds:

            def _ground_z(x: float, y: float) -> float:
                try:
                    z = self.world.ground_z_below(x, y, 5000.0, search=10000.0)
                    return float(z) if z is not None else 0.0
                except Exception:
                    return 0.0

            min_x, min_y = bounds["min_x"], bounds["min_y"]
            max_x, max_y = bounds["max_x"], bounds["max_y"]
            margin = bounds["margin"]

            perimeter_xy = [(min_x, min_y), (max_x, min_y), (max_x, max_y), (min_x, max_y)]
            margin_xy = [
                (min_x + margin, min_y + margin),
                (max_x - margin, min_y + margin),
                (max_x - margin, max_y - margin),
                (min_x + margin, max_y - margin),
            ]

            self._perimeter_corners = [(x, y, _ground_z(x, y)) for (x, y) in perimeter_xy]
            self._margin_corners = [(x, y, _ground_z(x, y)) for (x, y) in margin_xy]
            self.logger.info("boundary overlay ready (B = OSM perimeter, M = margin boundary)")
        else:
            self.logger.info("no staging bounds; boundary overlay unavailable (build a drape world first.)")

    def _toggle_layer(self, layer_name: str) -> None:
        """Toggle CARLA world layer visibility.

        Args:
            layer_name: Layer name ("photoreal", "ground" or "signals")
        """
        if self.world is None:
            return

        flag_name = f"{layer_name}_visible"
        new_value = self.toggle_flag(flag_name)
        try:
            self.world.set_layer_visible(layer_name, new_value)
            self.logger.info(f"layer '{layer_name}' visibility: {new_value}")
        except Exception as e:
            self.logger.error(f"set_layer_visible({layer_name}) failed: {e!r}")

    def _toggle_collision(self) -> None:
        """Toggle ground layer collision."""
        if self.world is None:
            return

        new_value = self.toggle_flag("ground_collision")
        try:
            self.world.set_layer_collision("ground", new_value)
            self.logger.info(f"ground collision: {new_value}")
        except Exception as e:
            self.logger.error(f"set_layer_collision(ground) failed: {e!r}")

    def _toggle_road(self) -> None:
        """Toggle road rendering."""
        if self.world is None:
            return

        new_value = self.toggle_flag("road_rendered")
        try:
            self.world.set_road_rendered(new_value)
            self.logger.info(f"road rendering: {new_value}")
        except Exception as e:
            self.logger.error(f"set_road_rendered failed: {e!r}")

    def _toggle_time_advance(self) -> None:
        """Toggle time advancement."""
        if self.world is None:
            return

        new_value = self.toggle_flag("time_advancing")
        try:
            self.world.set_time_advance(new_value, self.time_rate)
            self.logger.info(f"time advance: {new_value} (rate={self.time_rate}x)")
        except Exception as e:
            self.logger.error(f"set_time_advance failed: {e!r}")

    def _register_builtin_hotkeys(self) -> None:
        """Register built-in hotkeys for standard flags and world controls."""
        self.register_hotkey(pygame.K_b, lambda: self.toggle_flag("show_perimeter"))
        self.register_hotkey(pygame.K_m, lambda: self.toggle_flag("show_margin"))
        self.register_hotkey(pygame.K_c, lambda: self._toggle_layer("photoreal"))
        self.register_hotkey(pygame.K_g, lambda: self._toggle_layer("ground"))
        self.register_hotkey(pygame.K_v, self._toggle_collision)
        self.register_hotkey(pygame.K_r, self._toggle_road)
        self.register_hotkey(pygame.K_l, lambda: self._toggle_layer("signals"))
        self.register_hotkey(pygame.K_k, self._toggle_time_advance)

    def _register_subsystem_hotkeys(self) -> None:
        """Register hotkeys for subsystems."""
        if self.traffic:
            self.register_hotkey(pygame.K_t, lambda: self.traffic.toggle_want())
        if self.scenario:
            self.register_hotkey(pygame.K_x, lambda: self.scenario.toggle_want())
        if self.telemetry:
            self.register_hotkey(pygame.K_y, lambda: self.telemetry.toggle_want())
        if self.recorder:
            self.register_hotkey(pygame.K_f, lambda: self.recorder.toggle_want())
        if self.orbit_sensor_controller:
            self.register_hotkey(pygame.K_o, lambda: self.orbit_sensor_controller.toggle_orbit())
            self.register_hotkey(pygame.K_p, self._toggle_orbit_pause)

    def _toggle_orbit_pause(self) -> None:
        if not self.orbit_sensor_controller or not self.orbit_sensor_controller.orbit_enabled:
            return
        self.orbit_sensor_controller.toggle_orbit_pause()
        state = "paused" if self.orbit_sensor_controller.orbit_paused else "resumed"
        self.logger.info("orbit %s", state)

    def get_events(self) -> dict[str, Any]:
        """Process pygame events and return state changes.

        Returns:
            Dictionary with keys:
                - quit: bool, True if window closed or ESC pressed
                - mouse_look_delta: Optional[Tuple[int, int]], mouse movement if RMB held
                - mouse_wheel_delta: int, scroll wheel delta
                - pick_request: Optional[Tuple[int, int]], pixel coords if Ctrl+LMB
                - click_pos: Optional[Tuple[int, int]], pixel coords if LMB (no Ctrl)
                - reset_requested: bool, True if Space pressed
        """
        result = {
            "quit": False,
            "mouse_look_delta": None,
            "mouse_wheel_delta": 0,
            "pick_request": None,
            "click_pos": None,
            "reset_requested": False,
            "pressed_keys": None,
        }

        for ev in pygame.event.get():
            if ev.type == pygame.QUIT:
                result["quit"] = True

            elif ev.type == pygame.KEYDOWN:
                if ev.key == pygame.K_ESCAPE:
                    result["quit"] = True
                elif ev.key == pygame.K_SPACE:
                    result["reset_requested"] = True
                elif ev.key in self._hotkey_callbacks:
                    self._hotkey_callbacks[ev.key]()

            elif ev.type == pygame.MOUSEBUTTONDOWN:
                # CTL Click requests a "pick position", which draws a flyout on the screen
                if ev.button == 1 and (pygame.key.get_mods() & pygame.KMOD_CTRL):
                    result["pick_request"] = ev.pos

                # a regular click will close flyout if in the window
                elif ev.button == 1:
                    result["click_pos"] = ev.pos

                elif ev.button == 3:
                    self.looking = True
                    pygame.event.set_grab(True)
                    pygame.mouse.set_visible(False)
                    pygame.mouse.get_rel()

            elif ev.type == pygame.MOUSEBUTTONUP:
                if ev.button == 3:
                    self.looking = False
                    pygame.event.set_grab(False)
                    pygame.mouse.set_visible(True)

            elif ev.type == pygame.MOUSEWHEEL:
                result["mouse_wheel_delta"] = ev.y

        if self.looking:
            dx, dy = pygame.mouse.get_rel()
            if dx or dy:
                result["mouse_look_delta"] = (
                    dx * self.mouse_sensitivity,
                    dy * self.mouse_sensitivity,
                )

        # populate pressed keys
        result["pressed_keys"] = pygame.key.get_pressed()

        self.running = not result["quit"]
        self.last_events = result
        return result

    def process_events(self, events: dict[str, Any] | None = None) -> bool:
        """Process pygame events and return state changes.

        Args:
            events: Dictionary with keys:
                - quit: bool, True if window closed or ESC pressed
                - mouse_look_delta: Optional[Tuple[int, int]], mouse movement if RMB held
                - mouse_wheel_delta: int, scroll wheel delta
                - pick_request: Optional[Tuple[int, int]], pixel coords if Ctrl+LMB (request location of selected pixel)
                - click_pos: Optional[Tuple[int, int]], pixel coords if LMB (no Ctrl) ( )
                - reset_requested: bool, True if Space pressed
            If None, will use the events from self.get_events()

        Returns:
            quit: bool, True if window closed or ESC pressed, this should quit the main thread
        """

        dt = self.tick()
        if events is None:
            events = self.get_events()

        # check for quick exit
        if events["quit"]:
            return True

        if events["reset_requested"] and self.sensors:
            self.sensors.reset_to_initial_pose()
            self.logger.info("sensor reset to initial pose")

        # handle pick request
        if events["pick_request"] and self.sensors:
            try:
                try:
                    lat0, lon0, origin_h = self.world.get_cesium_origin()
                except Exception:
                    lat0 = lon0 = origin_h = None
                pick_result = self.sensors.pick_world_point(
                    events["pick_request"][0],
                    events["pick_request"][1],
                    origin_lat=lat0,
                    origin_lon=lon0,
                    origin_h=origin_h,
                )
                self.pick_result = pick_result
                self.note = None
                if pick_result.get("lat") is not None and pick_result.get("lon") is not None:
                    self.logger.info(
                        "world pick: from pixel location %s -> lat=%.7f, lon=%.7f, elev=%.1fm",
                        events["pick_request"],
                        pick_result["lat"],
                        pick_result["lon"],
                        pick_result["elev_m"],
                    )
                else:
                    self.logger.info(
                        "world pick: from pixel location %s -> elev=%.1fm (georeference unavailable)",
                        events["pick_request"],
                        pick_result["elev_m"],
                    )
            except ValueError as e:
                self.note = (str(e), time.time())
                self.logger.info(f"world pick failed: {e}")

        if events["click_pos"]:
            r = self.pick_close_reigon
            if (
                self.pick_result
                and r
                and r[0] <= events["click_pos"][0] < r[0] + r[2]
                and r[1] <= events["click_pos"][1] < r[1] + r[3]
            ):
                self.pick_result = None
                self.note = None
                self.logger.info("pick result closed")

        if self.controller:
            self.controller.update_movement(dt, events)

        return False


    def render(self):
        # blit surface from sensor subsystem
        if self.sensors:
            rgb_image = self.sensors.get_latest_rgb()
            if rgb_image is not None:
                self.blit_surface(PygameInterface.image_to_surface(rgb_image))

            # get position and render boundry overlays
            if self.orbit_sensor_controller and self.orbit_sensor_controller.orbit_enabled:
                cam_pose = Pose.from_carla_transform(
                    self.orbit_sensor_controller.controlled_object.get_current_transform()
                )
            else:
                cam_pose = self.sensors.get_position()
            cam_xyz = (cam_pose.x, cam_pose.y, cam_pose.z)
            self.render_boundary_overlays(cam_xyz, cam_pose.yaw, cam_pose.pitch)

            ft_per_m = self.sensors.FT_PER_M
            try:
                _, _, origin_h = self.world.get_cesium_origin()
            except Exception:
                origin_h = 0.0
            elev_ft = (origin_h + cam_pose.z) * ft_per_m
            gz = self.sensors.ground_z
            agl_ft = (cam_pose.z - gz) * ft_per_m if gz is not None else None
            agl_str = f"{agl_ft:5.0f}" if agl_ft is not None else "   --"
        else:
            cam_pose = None
            elev_ft = 0.0
            agl_str = "   --"

        # poll solar for time
        solar_hud = ""  # "HH:MM" refreshed from get_solar_state at low frequency
        self.solar_poll_frame += 1
        if self.solar_poll_frame >= 30:
            self.solar_poll_frame = 0
            try:
                _ss = self.world.get_solar_state()
                if _ss:
                    _h = _ss["solar_time"]
                    solar_hud = f"{int(_h) % 24:02d}:{int((_h % 1) * 60) % 60:02d}"
            except Exception:
                pass
        time_str = f"{solar_hud or '--:--'}" + (
            f" >{self.time_rate:g}x" if self.get_flag("time_advancing") else ""
        )

        # create traffic, telem, and recording info strings
        if self.traffic:
            traf_str = (
                f"{self.traffic.count()}/{self.traffic.args.max}"
                if self.traffic.enabled
                else ("OFF" if self.traffic.available else "n/a")
            )
        else:
            traf_str = "n/a"

        if self.telemetry:
            tel_str = (
                "ON" if self.telemetry.enabled else ("OFF" if self.telemetry.available else "n/a")
            )
        else:
            tel_str = "n/a"

        if self.recorder:
            rec_str = (
                f"REC {self.recorder.saved}@{self.recorder.record_hz:g}Hz"
                if self.recorder.recording
                else "off"
            )
        else:
            rec_str = "n/a"

        if self.scenario:
            if self.scenario.running:
                scen_str = self.scenario.status()
            elif self.scenario.available:
                scen_str = "armed"
            else:
                scen_str = "n/a"
        else:
            scen_str = "n/a"

        orbit_info = (
            self.orbit_sensor_controller.get_hud_info() if self.orbit_sensor_controller else None
        )
        orbit_enabled = bool(orbit_info and orbit_info["orbit_enabled"])
        orbit_str = "ON" if orbit_enabled else "OFF"

        # slap it all together
        if cam_pose:
            pose_str = (
                f"elev {elev_ft:6.0f} ft   AGL {agl_str} ft   x {cam_pose.x:7.1f}  N {-cam_pose.y:7.1f}   "
                f"yaw {cam_pose.yaw:6.1f} pitch {cam_pose.pitch:6.1f}   [{'SYNC' if self.sync else 'ASYNC'}]"
            )
            speed_val = self.controller.speed if self.controller else 0.0
            frames_val = self.sensors.frames_received if self.sensors else 0
        else:
            pose_str = "no sensors   [{'SYNC' if self.sync else 'ASYNC'}]"
            speed_val = 0.0
            frames_val = 0

        hud = [
            pose_str,
            f"speed {speed_val:4.0f}   photoreal(C) {'ON' if self.get_flag('photoreal_visible') else 'OFF'}   "
            f"ground(G) {'ON' if self.get_flag('ground_visible') else 'OFF'}   gColl(V) {'ON' if self.get_flag('ground_collision') else 'OFF'}   "
            f"road(R) {'ON' if self.get_flag('road_rendered') else 'OFF'}   signals(L) {'ON' if self.get_flag('signals_visible') else 'OFF'}   "
            f"perim(B) {'ON' if self.get_flag('show_perimeter') else 'OFF'}   "
            f"margin(M) {'ON' if self.get_flag('show_margin') else 'OFF'}   time(K) {time_str}",
            f"traffic(T) {traf_str}   scenario(X) {scen_str}   telemetry(Y) {tel_str}   record(F) {rec_str}   "
            f"orbit(O) {orbit_str}   "
            f"fps {self.get_fps():4.0f}   frames {frames_val}",
            "RMB look | Ctrl+LMB measure | WASD/EQ fly | wheel speed | Shift fast | C/G/V/R/L/B/M layers | ",
            "K time | T traffic | X scenario | Y telemetry | F record | O orbit | P pause orbit | Space reset | Esc quit",
        ]

        if orbit_enabled and orbit_info is not None:
            center_latlon = orbit_info.get("center_latlon")
            if center_latlon is not None:
                center_lat, center_lon = center_latlon
            else:
                center_lat, center_lon = None, None
            if center_lat is not None and center_lon is not None:
                latlon_str = f"lat {center_lat:.7f}, lon {center_lon:.7f}"
            else:
                latlon_str = "lat/lon unavailable"
            orbit_status = "PAUSED" if orbit_info["orbit_paused"] else "ACTIVE"
            orbit_line = (
                f"ORBIT: center ({orbit_info['orbit_center'][0]:7.1f}, {orbit_info['orbit_center'][1]:7.1f})   "
                f"radius {orbit_info['radius_feet']:6.0f} ft   altitude {orbit_info['cam_altitude_feet']:6.0f} ft   "
                f"{latlon_str}   progress {orbit_info['orbit_progress']:5.1f}%   "
                f"speed {orbit_info['orbit_speed']:5.1f} s   {orbit_status}"
            )
            hud.insert(-2, orbit_line)

        # draw hud
        bar_h = self.draw_hud_bar(hud, y_offset=0)

        if cam_pose:
            yaw_r = math.radians(cam_pose.yaw)
            bearing = math.degrees(math.atan2(math.cos(yaw_r), -math.sin(yaw_r))) % 360.0
            self.draw_compass(self.width - 52, bar_h + 48, 40, bearing)
            if orbit_enabled and orbit_info is not None:
                self.draw_orbit_viz(60, bar_h + 48, 40, orbit_info["angle"])

        # draws a "flyout" with information about the picked point
        pick = self.pick_result
        if pick is not None:
            self.pick_close_reigon = self.draw_flyout(
                pick["u"], pick["v"], pick["lat"], pick["lon"], pick["elev_ft"], pick["elev_m"]
            )

        # draw any notices to the user
        if self.note is not None:
            if time.time() - self.note[1] < 3.0:
                self.draw_text(self.note[0], 8, bar_h + 6, (255, 120, 120))

        pygame.display.flip()

    def tick(self) -> float:
        """Advance the clock and return delta time in seconds since last tick"""
        return self.clock.tick(self.target_fps) / 1000.0

    def get_fps(self) -> float:
        """Get current frames per second."""
        return self.clock.get_fps()

    def clear(self, color: tuple[int, int, int] = (0, 0, 0)) -> None:
        """Clear the display to a solid color"""
        self.display.fill(color)

    def blit_surface(self, surface: pygame.Surface, pos: tuple[int, int] = (0, 0)) -> None:
        """Blit a surface to the display at a given position."""
        self.display.blit(surface, pos)

    def quit(self) -> None:
        """Shutdown pygame."""
        self.logger.info("pygame interface shutdown")
        pygame.quit()

    @staticmethod
    def image_to_surface(image: Any) -> pygame.Surface:
        """Convert a CARLA camera image to a pygame surface.

        Args:
            image: CARLA sensor image with raw_data, width, height attributes

        Returns:
            Pygame surface ready for blitting
        """
        arr = np.frombuffer(bytes(image.raw_data), dtype=np.uint8)
        arr = np.reshape(arr, (image.height, image.width, 4))
        arr = arr[:, :, :3][:, :, ::-1]
        return pygame.surfarray.make_surface(arr.swapaxes(0, 1))

    def draw_compass(self, cx: int, cy: int, radius: int, bearing_deg: float) -> None:
        """Draw a north-up compass rose.

        Args:
            cx: Center X pixel
            cy: Center Y pixel
            radius: Compass radius in pixels
            bearing_deg: Camera heading (0=N, 90=E, clockwise)
        """
        b = math.radians(bearing_deg)
        bg = pygame.Surface((2 * radius + 4, 2 * radius + 4), pygame.SRCALPHA)
        pygame.draw.circle(bg, (0, 0, 0, 150), (radius + 2, radius + 2), radius)
        pygame.draw.circle(bg, (0, 255, 255), (radius + 2, radius + 2), radius, 1)
        self.display.blit(bg, (cx - radius - 2, cy - radius - 2))

        for label, cb, col in (
            ("N", 0, (255, 80, 80)),
            ("E", 90, (220, 220, 220)),
            ("S", 180, (220, 220, 220)),
            ("W", 270, (220, 220, 220)),
        ):
            a = math.radians(cb - bearing_deg)
            lx = cx + math.sin(a) * (radius - 10)
            ly = cy - math.cos(a) * (radius - 10)
            s = self.font.render(label, True, col)
            self.display.blit(s, (lx - s.get_width() / 2, ly - s.get_height() / 2))

        pygame.draw.line(
            self.display,
            (255, 80, 80),
            (cx, cy),
            (cx - math.sin(b) * (radius - 4), cy - math.cos(b) * (radius - 4)),
            2,
        )
        pygame.draw.line(
            self.display,
            (160, 160, 160),
            (cx, cy),
            (cx + math.sin(b) * (radius - 4), cy + math.cos(b) * (radius - 4)),
            2,
        )
        pygame.draw.circle(self.display, (0, 255, 255), (cx, cy), 2)

        hdg = self.font.render(f"{bearing_deg:03.0f}", True, (255, 255, 0))
        self.display.blit(hdg, (cx - hdg.get_width() / 2, cy + radius + 2))

    def draw_orbit_viz(self, cx: int, cy: int, radius: int, angle: float) -> None:
        pygame.draw.circle(self.display, (100, 100, 100), (cx, cy), radius, 1)
        pygame.draw.circle(self.display, (255, 80, 80), (cx, cy), 3)
        viz_cam_x = cx + int(radius * math.cos(angle))
        viz_cam_y = cy + int(radius * math.sin(angle))
        pygame.draw.circle(self.display, (0, 255, 255), (viz_cam_x, viz_cam_y), 4)
        pygame.draw.line(self.display, (0, 255, 255), (viz_cam_x, viz_cam_y), (cx, cy), 1)

    def draw_hud_bar(self, lines: list[str], y_offset: int = 0) -> int:
        """Draw a semi-transparent HUD bar with text lines.

        Args:
            lines: List of text lines to render
            y_offset: Vertical offset from top of screen

        Returns:
            Height of the rendered bar in pixels
        """
        bar_h = 8 + len(lines) * 18 + 2
        bar = pygame.Surface((self.width, bar_h))
        bar.set_alpha(180)
        bar.fill((0, 0, 0))
        self.display.blit(bar, (0, y_offset))

        for i, line in enumerate(lines):
            self.display.blit(
                self.font.render(line, True, (255, 255, 0)),
                (8, y_offset + 8 + i * 18),
            )

        return bar_h

    def draw_flyout(
        self,
        pick_u: int,
        pick_v: int,
        lat: float | None,
        lon: float | None,
        elev_ft: float,
        elev_m: float,
    ) -> tuple[int, int, int, int]:
        """Draw a lat/lon/elev picker flyout window with close button.

        Args:
            pick_u: Pixel X coordinate of pick
            pick_v: Pixel Y coordinate of pick
            lat: Latitude (or None)
            lon: Longitude (or None)
            elev_ft: Elevation in feet
            elev_m: Elevation in meters

        Returns:
            Close button rect (x, y, w, h) for hit testing
        """
        pygame.draw.line(self.display, (0, 255, 255), (pick_u - 8, pick_v), (pick_u + 8, pick_v), 1)
        pygame.draw.line(self.display, (0, 255, 255), (pick_u, pick_v - 8), (pick_u, pick_v + 8), 1)
        pygame.draw.circle(self.display, (0, 255, 255), (pick_u, pick_v), 3, 1)

        lines = [
            f"lat  {lat:11.7f}" if lat is not None else "lat        --",
            f"lon  {lon:11.7f}" if lon is not None else "lon        --",
            f"elev {elev_ft:6.0f} ft  ({elev_m:.1f} m)",
        ]
        surfs = [self.font.render(s, True, (255, 255, 0)) for s in lines]

        pad = 6
        btn = 14
        header_h = btn + 2
        pw = max(max(s.get_width() for s in surfs), 60) + pad * 2
        ph = pad + header_h + sum(s.get_height() for s in surfs) + pad
        px = max(0, min(pick_u + 12, self.width - pw))
        py = max(0, min(pick_v + 12, self.height - ph))

        panel = pygame.Surface((pw, ph))
        panel.set_alpha(200)
        panel.fill((0, 0, 0))
        self.display.blit(panel, (px, py))
        pygame.draw.rect(self.display, (0, 255, 255), (px, py, pw, ph), 1)

        bx = px + pw - pad - btn
        by = py + pad
        pygame.draw.rect(self.display, (0, 255, 255), (bx, by, btn, btn), 1)
        pygame.draw.line(
            self.display, (0, 255, 255), (bx + 3, by + 3), (bx + btn - 3, by + btn - 3), 1
        )
        pygame.draw.line(
            self.display, (0, 255, 255), (bx + btn - 3, by + 3), (bx + 3, by + btn - 3), 1
        )

        y = py + pad + header_h
        for s in surfs:
            self.display.blit(s, (px + pad, y))
            y += s.get_height()

        close_box = (bx, by, btn, btn)
        return close_box

    def draw_text(
        self,
        text: str,
        x: int,
        y: int,
        color: tuple[int, int, int] = (255, 255, 255),
    ) -> None:
        """Draw text at a specific position.

        Args:
            text: Text to render
            x: X pixel position
            y: Y pixel position
            color: RGB color tuple
        """
        surf = self.font.render(text, True, color)
        self.display.blit(surf, (x, y))

    @staticmethod
    def world_to_pixel(
        world_pt: tuple[float, float, float],
        cam_pos: tuple[float, float, float],
        fwd: tuple[float, float, float],
        right: tuple[float, float, float],
        up: tuple[float, float, float],
        focal_length: float,
        cx: float,
        cy: float,
    ) -> tuple[int, int] | None:
        """Project a world point to screen pixel coordinates.

        Args:
            world_pt: (x, y, z) world coordinates
            cam_pos: (x, y, z) camera position
            fwd: Forward unit vector
            right: Right unit vector
            up: Up unit vector
            focal_length: Focal length in pixels
            cx: Principal point X
            cy: Principal point Y

        Returns:
            (u, v) pixel coordinates, or None if behind camera or far off-axis
        """
        dx = world_pt[0] - cam_pos[0]
        dy = world_pt[1] - cam_pos[1]
        dz = world_pt[2] - cam_pos[2]
        depth = dx * fwd[0] + dy * fwd[1] + dz * fwd[2]

        if depth <= 0.5:
            return None

        sr = (dx * right[0] + dy * right[1] + dz * right[2]) / depth
        su = (dx * up[0] + dy * up[1] + dz * up[2]) / depth

        if abs(sr) > 6.0 or abs(su) > 6.0:
            return None

        return (int(cx + sr * focal_length), int(cy - su * focal_length))

    def draw_boundary(
        self,
        corners: list[tuple[float, float, float]],
        color: tuple[int, int, int],
        cam_pos: tuple[float, float, float],
        yaw: float,
        pitch: float,
        focal_length: float,
        cx: float,
        cy: float,
        posts: bool = False,
    ) -> None:
        """Draw a projected ground rectangle as an overlay.

        Args:
            corners: List of 4 (x, y, z) world coordinates
            color: RGB color tuple
            cam_pos: Camera (x, y, z) position
            yaw: Camera yaw in degrees
            pitch: Camera pitch in degrees
            focal_length: Focal length in pixels
            cx: Principal point X
            cy: Principal point Y
            posts: If True, draw vertical posts at corners
        """
        yr = math.radians(yaw)
        pr = math.radians(pitch)
        fwd = (math.cos(yr) * math.cos(pr), math.sin(yr) * math.cos(pr), math.sin(pr))
        right = (-math.sin(yr), math.cos(yr), 0.0)
        up = (
            fwd[1] * right[2] - fwd[2] * right[1],
            fwd[2] * right[0] - fwd[0] * right[2],
            fwd[0] * right[1] - fwd[1] * right[0],
        )

        n_segments = 24
        for i in range(4):
            a = corners[i]
            b = corners[(i + 1) % 4]
            prev = None
            for t in range(n_segments + 1):
                s = t / n_segments
                point = (
                    a[0] + (b[0] - a[0]) * s,
                    a[1] + (b[1] - a[1]) * s,
                    a[2] + (b[2] - a[2]) * s,
                )
                scr = self.world_to_pixel(point, cam_pos, fwd, right, up, focal_length, cx, cy)
                if scr is not None and prev is not None:
                    pygame.draw.line(self.display, color, prev, scr, 3)
                prev = scr

        if posts:
            for c in corners:
                base = self.world_to_pixel(c, cam_pos, fwd, right, up, focal_length, cx, cy)
                top = self.world_to_pixel(
                    (c[0], c[1], c[2] + 25.0), cam_pos, fwd, right, up, focal_length, cx, cy
                )
                if base is not None:
                    pygame.draw.circle(self.display, color, base, 6, 2)
                    if top is not None:
                        pygame.draw.line(self.display, color, base, top, 3)

    def render_boundary_overlays(
        self,
        cam_pos: tuple[float, float, float],
        yaw: float,
        pitch: float,
    ) -> None:
        """Render perimeter and margin boundary overlays if enabled.

        Uses internal corner state and projection parameters set by setup_boundary_overlays().

        Args:
            cam_pos: Camera (x, y, z) position
            yaw: Camera yaw in degrees
            pitch: Camera pitch in degrees
        """
        if self._proj_focal_length is None:
            return

        if self.get_flag("show_perimeter") and self._perimeter_corners:
            self.draw_boundary(
                self._perimeter_corners,
                (255, 50, 50),
                cam_pos,
                yaw,
                pitch,
                self._proj_focal_length,
                self._proj_cx,
                self._proj_cy,
                posts=True,
            )

        if self.get_flag("show_margin") and self._margin_corners:
            self.draw_boundary(
                self._margin_corners,
                (40, 160, 255),
                cam_pos,
                yaw,
                pitch,
                self._proj_focal_length,
                self._proj_cx,
                self._proj_cy,
                posts=False,
            )
