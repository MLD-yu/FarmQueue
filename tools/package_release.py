import hashlib
import json
import os
import shutil
import zipfile

SRC = r"C:\Users\21092\OneDrive\Desktop\farm queue\v0.4.0"
OUT = r"C:\Users\21092\OneDrive\Desktop\FarmQueue 0.5.1.zip"
STAGE = r"C:\Users\21092\OneDrive\Desktop\farm queue\.pack-0.5.1"

dll_src = os.path.join(SRC, "obj", "Debug", "net6.0", "FarmQueue.dll")

# 1) 暂存目录
if os.path.isdir(STAGE):
    shutil.rmtree(STAGE)
mod = os.path.join(STAGE, "FarmQueue")
os.makedirs(os.path.join(mod, "i18n"))

shutil.copy2(os.path.join(SRC, "manifest.json"), os.path.join(mod, "manifest.json"))
shutil.copy2(dll_src, os.path.join(mod, "FarmQueue.dll"))
for name in ("default.json", "zh.json"):
    shutil.copy2(os.path.join(SRC, "i18n", name), os.path.join(mod, "i18n", name))

# 2) 校验 manifest 里的版本
with open(os.path.join(mod, "manifest.json"), encoding="utf-8") as f:
    manifest = json.load(f)
print("manifest version:", manifest["Version"])

# 3) 打包（结构与 0.5.0 一致：FarmQueue/manifest.json、FarmQueue/FarmQueue.dll、FarmQueue/i18n/*）
if os.path.exists(OUT):
    os.remove(OUT)
with zipfile.ZipFile(OUT, "w", zipfile.ZIP_DEFLATED) as z:
    for root, _dirs, files in os.walk(STAGE):
        for name in sorted(files):
            full = os.path.join(root, name)
            arc = os.path.relpath(full, STAGE).replace("\\", "/")
            z.write(full, arc)

# 4) 校验包
with zipfile.ZipFile(OUT) as z:
    print("--- zip 内容 ---")
    for i in z.infolist():
        print("  %-34s %8d bytes" % (i.filename, i.file_size))
    bad = z.testzip()
    print("zip 完整性:", "OK" if bad is None else bad)
    with z.open("FarmQueue/manifest.json") as f:
        print("包内 manifest 版本:", json.loads(f.read().decode("utf-8"))["Version"])

shutil.rmtree(STAGE)

h = hashlib.md5(open(dll_src, "rb").read()).hexdigest()
print("dll md5:", h)
print("包大小: %.1f KB" % (os.path.getsize(OUT) / 1024.0))
print("输出:", OUT)
