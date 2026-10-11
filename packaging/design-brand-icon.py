from pathlib import Path
from PIL import Image,ImageDraw
svg='<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64"><rect x="2" y="2" width="60" height="60" rx="16" fill="#ba3157"/><path d="M17 27v10m9-17v24" fill="none" stroke="#fff" stroke-width="5" stroke-linecap="round"/><path d="M37 20v24l17-12z" fill="#fff"/></svg>'
Path('app/assets/brand.svg').write_text(svg,encoding='utf-8')
im=Image.new('RGBA',(1024,1024));d=ImageDraw.Draw(im);d.rounded_rectangle((32,32,992,992),radius=256,fill='#ba3157')
for x,y1,y2 in [(272,432,592),(416,320,704)]:
 d.line((x,y1,x,y2),fill='white',width=80);d.ellipse((x-40,y1-40,x+40,y1+40),fill='white');d.ellipse((x-40,y2-40,x+40,y2+40),fill='white')
d.polygon([(592,320),(592,704),(864,512)],fill='white')
im.resize((512,512),Image.Resampling.LANCZOS).save('app/assets/BgmHotkey.png')
im.resize((256,256),Image.Resampling.LANCZOS).save('app/assets/BgmHotkey.ico',sizes=[(16,16),(20,20),(24,24),(32,32),(40,40),(48,48),(64,64),(128,128),(256,256)])
print('Generated SVG, PNG and multi-resolution ICO')
