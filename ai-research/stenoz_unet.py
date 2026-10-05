"""Research-only U-Net architecture compatible with the Stenoz ARCADE checkpoint.

This adapter is for demonstration/research use only. It is not a medical device and
must not be used as a standalone diagnostic system.
"""
from __future__ import annotations

import torch
import torch.nn as nn


def _conv_block(in_channels: int, out_channels: int) -> nn.Sequential:
    return nn.Sequential(
        nn.Conv2d(in_channels, out_channels, kernel_size=3, padding=1, bias=False),
        nn.BatchNorm2d(out_channels),
        nn.ReLU(inplace=True),
        nn.Conv2d(out_channels, out_channels, kernel_size=3, padding=1, bias=False),
        nn.BatchNorm2d(out_channels),
        nn.ReLU(inplace=True),
    )


class StenozCompatibleUNet(nn.Module):
    """Layer names intentionally match the published Stenoz checkpoint."""

    def __init__(self, base: int = 32) -> None:
        super().__init__()
        b = int(base)
        self.e1 = _conv_block(1, b)
        self.e2 = _conv_block(b, b * 2)
        self.e3 = _conv_block(b * 2, b * 4)
        self.e4 = _conv_block(b * 4, b * 8)
        self.pool = nn.MaxPool2d(2)
        self.bott = _conv_block(b * 8, b * 16)
        self.u4 = nn.ConvTranspose2d(b * 16, b * 8, kernel_size=2, stride=2)
        self.d4 = _conv_block(b * 16, b * 8)
        self.u3 = nn.ConvTranspose2d(b * 8, b * 4, kernel_size=2, stride=2)
        self.d3 = _conv_block(b * 8, b * 4)
        self.u2 = nn.ConvTranspose2d(b * 4, b * 2, kernel_size=2, stride=2)
        self.d2 = _conv_block(b * 4, b * 2)
        self.u1 = nn.ConvTranspose2d(b * 2, b, kernel_size=2, stride=2)
        self.d1 = _conv_block(b * 2, b)
        self.out = nn.Conv2d(b, 1, kernel_size=1)

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        e1 = self.e1(x)
        e2 = self.e2(self.pool(e1))
        e3 = self.e3(self.pool(e2))
        e4 = self.e4(self.pool(e3))
        bottleneck = self.bott(self.pool(e4))
        x = self.d4(torch.cat((self.u4(bottleneck), e4), dim=1))
        x = self.d3(torch.cat((self.u3(x), e3), dim=1))
        x = self.d2(torch.cat((self.u2(x), e2), dim=1))
        x = self.d1(torch.cat((self.u1(x), e1), dim=1))
        return self.out(x)
