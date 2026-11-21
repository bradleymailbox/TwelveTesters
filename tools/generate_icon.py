from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

out = Path(__file__).resolve().parents[1] / 'TwelveTestersTray' / 'app.ico'
size = 256
im = Image.new('RGBA', (size, size), (0, 0, 0, 0))
d = ImageDraw.Draw(im)
d.ellipse((5, 5, 250, 250), fill=(214, 84, 30, 255))
font_path = Path('C:/Windows/Fonts/segoeuib.ttf')
font = ImageFont.truetype(str(font_path), 133)
bbox = d.textbbox((0, 0), '12', font=font)
x = (size - (bbox[2] - bbox[0])) / 2 - bbox[0]
y = (size - (bbox[3] - bbox[1])) / 2 - bbox[1] - 5
d.text((x, y), '12', font=font, fill='white')
im.save(out, format='ICO', sizes=[(16, 16), (32, 32), (48, 48), (256, 256)])
print(out)
