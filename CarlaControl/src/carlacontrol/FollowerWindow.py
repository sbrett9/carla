"""The pygame window a camera follower shows its picture in."""

from __future__ import annotations

import logging
from typing import Any

import numpy as np
import pygame


class FollowerWindow:
    """Shows a camera frame with a few lines of text over it, and reports when to quit.

    Deliberately not `PygameInterface`: that window's hotkeys toggle map layers, collision and the
    sun's advance, and a follower must never touch any of them. This one has three ways out and no
    other keys: Esc, Q, or closing the window.
    """

    QUIT_KEYS = (pygame.K_ESCAPE, pygame.K_q)
    DISPLAY_HZ = 60
    TEXT_COLOUR = (255, 255, 0)
    BAR_ALPHA = 180

    def __init__(self, font_name: str = "consolas", font_size: int = 16) -> None:
        self.logger = logging.getLogger(__name__)
        self.font_name = font_name
        self.font_size = font_size
        self.display: pygame.Surface | None = None
        self.font: pygame.font.Font | None = None
        self.clock: pygame.time.Clock | None = None

    def open(self, width: int, height: int, title: str) -> None:
        """Open the window at the camera's own size."""
        pygame.display.init()
        pygame.font.init()
        self.font = pygame.font.SysFont(self.font_name, self.font_size)
        self.display = pygame.display.set_mode((width, height))
        pygame.display.set_caption(title)
        self.clock = pygame.time.Clock()

    def quit_requested(self) -> bool:
        """Drain the window's events; True when the window was closed or Esc or Q was pressed."""
        requested = False
        for event in pygame.event.get():
            if event.type == pygame.QUIT or (
                    event.type == pygame.KEYDOWN and event.key in self.QUIT_KEYS):
                requested = True
        return requested

    def show_frame(self, image: Any, overlay: list[str]) -> None:
        """Draw a BGRA camera frame, then the overlay lines on a translucent bar, and flip."""
        pixels = np.frombuffer(bytes(image.raw_data), dtype=np.uint8)
        pixels = pixels.reshape((image.height, image.width, 4))[:, :, 2::-1]
        self.display.blit(pygame.surfarray.make_surface(pixels.swapaxes(0, 1)), (0, 0))
        self._draw_lines(overlay)
        pygame.display.flip()

    def show_message(self, lines: list[str]) -> None:
        """Clear the window and show only these lines; used before any frame exists."""
        self.display.fill((0, 0, 0))
        self._draw_lines(lines)
        pygame.display.flip()

    def pace(self) -> None:
        """Hold the display loop to a steady rate so it does not spin a core."""
        self.clock.tick(self.DISPLAY_HZ)

    def close(self) -> None:
        """Close the window."""
        pygame.display.quit()
        pygame.font.quit()

    def _draw_lines(self, lines: list[str]) -> None:
        line_h = self.font.get_linesize()
        bar = pygame.Surface((self.display.get_width(), 8 + line_h * len(lines)))
        bar.set_alpha(self.BAR_ALPHA)
        bar.fill((0, 0, 0))
        self.display.blit(bar, (0, 0))
        for index, line in enumerate(lines):
            self.display.blit(self.font.render(line, True, self.TEXT_COLOUR),
                              (8, 4 + index * line_h))
