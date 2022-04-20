# Mirror-vs-Fishnet
 Comparing performance between Mirror and Fishnet networking libraries.
 I added custom scripting symbol called SERVER_BUILD because I built client and server using headless build option
 and NetworkManager was starting server if I didn't do that.
```
Mirror(v66.0.9) + 200 clients => Performance lost was about %85
Fishnet(v1.4.3) + 200 clients => Performance lost was about %30

Unity 2020.3.30f1
Clients target frame rate 15 FPS
Server target frame rate 60 FPS
```
Here is the bat script to open 100 clients:
```
@echo off
for /L %%a in (1,1,100) do (
   echo This is iteration %%a
   start mirror-fps-prediction.exe
)
pause
```
