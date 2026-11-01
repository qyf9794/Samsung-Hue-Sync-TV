#!/usr/bin/env python3
"""Create the original, local ambient-TV application icon."""
from PIL import Image, ImageDraw, ImageFilter
from pathlib import Path
from math import sqrt

S=1024
img=Image.new('RGB',(S,S))
p=img.load()
for y in range(S):
    for x in range(S):
        t=(x+y)/(2*S)
        glow=max(0,1-sqrt(((x-512)/700)**2+((y-440)/580)**2))
        p[x,y]=(int(8+8*glow+5*t),int(12+16*glow+4*t),int(26+35*glow+5*t))

# Soft colored light behind the screen, with rounded corners and an open bottom.
glow=Image.new('RGBA',(S,S));d=ImageDraw.Draw(glow)
d.line([(215,675),(180,675),(180,330),(180,295),(330,270)],fill=(255,77,122,235),width=75,joint='curve')
d.line([(255,270),(480,250),(700,270)],fill=(177,96,255,240),width=75,joint='curve')
d.line([(690,270),(845,295),(845,675),(810,675)],fill=(36,203,244,235),width=75,joint='curve')
glow=glow.filter(ImageFilter.GaussianBlur(45));img=Image.alpha_composite(img.convert('RGBA'),glow)
d=ImageDraw.Draw(img)
# Luminous edge accents communicate light emitted behind a TV.
d.rounded_rectangle((155,235,870,710),radius=92,outline=(68,91,130,150),width=3)
d.arc((155,235,870,710),190,265,fill=(255,99,138,255),width=22)
d.arc((155,235,870,710),270,345,fill=(64,205,246,255),width=22)
d.rounded_rectangle((210,285,815,660),radius=50,fill=(8,15,31,255),outline=(218,231,255,255),width=18)
d.rounded_rectangle((232,307,793,638),radius=32,fill=(22,36,61,255))
# Three broad bands imply changing light, without copying any third-party branding.
layer=Image.new('RGBA',(S,S));ld=ImageDraw.Draw(layer)
ld.ellipse((245,370,535,660),fill=(252,78,126,215))
ld.ellipse((375,310,665,650),fill=(146,96,245,205))
ld.ellipse((515,340,805,660),fill=(47,191,235,200))
layer=layer.filter(ImageFilter.GaussianBlur(35))
mask=Image.new('L',(S,S));ImageDraw.Draw(mask).rounded_rectangle((233,308,792,637),radius=30,fill=255)
img.paste(layer,(0,0),Image.composite(layer.getchannel('A'),Image.new('L',(S,S)),mask))
d=ImageDraw.Draw(img)
d.rounded_rectangle((478,660,548,737),radius=10,fill=(218,231,255,255))
d.rounded_rectangle((400,728,625,751),radius=11,fill=(218,231,255,255))
img.convert('RGB').resize((512,512),Image.Resampling.LANCZOS).save(Path(__file__).parent/'tizen-web'/'icon.png',optimize=True)
