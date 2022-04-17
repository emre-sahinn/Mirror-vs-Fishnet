@echo off
cd /d "C:\bin\phantomjs-1.9.2-windows"
for /L %%a in (1,1,100) do (
   echo This is iteration %%a
   start mirror-fps-prediction.exe
)
pause