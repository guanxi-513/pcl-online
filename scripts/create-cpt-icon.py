"""Replace the existing badge lettering deterministically, retaining its outline."""
from pathlib import Path
import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFont

root = Path(__file__).resolve().parents[1]
source = Image.open(root / 'Plain Craft Launcher 2/Images/icon.ico').convert('RGBA')
pixels = np.array(source)
yy, xx = np.mgrid[:256, :256]
x, y = xx / 256, yy / 256
features = np.stack((np.ones_like(x), x, y, x*x, x*y, y*y, x*x*x, x*x*y, x*y*y, y*y*y), axis=-1)
inside = (xx > 48) & (xx < 207) & (yy > 60) & (yy < 195)
lettering = (xx >= 45) & (xx <= 207) & (yy >= 86) & (yy <= 174)
samples = inside & ~lettering
coefficients = np.linalg.lstsq(features[samples], pixels[:, :, :3][samples], rcond=None)[0]
background = np.clip(features @ coefficients, 0, 255)
mask = lettering.astype(np.uint8) * 255
mask = cv2.GaussianBlur(mask, (9, 9), 2) / 255
rgb = np.clip(background * mask[:, :, None] + pixels[:, :, :3] * (1-mask[:, :, None]), 0, 255).astype(np.uint8)
badge = Image.fromarray(np.dstack((rgb, pixels[:, :, 3]))).resize((1024, 1024), Image.Resampling.LANCZOS)
font = ImageFont.truetype('C:/Windows/Fonts/segoeuib.ttf', 250)
draw = ImageDraw.Draw(badge)
bounds = draw.textbbox((0, 0), 'CPT', font=font)
width, height = bounds[2] - bounds[0], bounds[3] - bounds[1]
draw.text(((1024 - width) / 2 - bounds[0], 508 - height / 2 - bounds[1]), 'CPT',
          font=font, fill='white', stroke_width=2, stroke_fill=(225, 238, 255))
images = root / 'Plain Craft Launcher 2/Images'
badge.save(images / 'Heads/CPT.png')
badge.save(images / 'CPT.ico', sizes=[(size, size) for size in (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)])
print('CPT icon and PNG generated.')
