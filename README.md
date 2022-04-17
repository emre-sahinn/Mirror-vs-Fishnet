# Mirror-vs-Fishnet
 Comparing performance between Mirror and Fishnet networking libraries

Here is the bat script to open 100 clients:
```
@echo off
for /L %%a in (1,1,100) do (
   echo This is iteration %%a
   start mirror-fps-prediction.exe
)
pause
```
