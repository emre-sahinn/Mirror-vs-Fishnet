# mirror-fps-prediction
mirror-fps-prediction

# Usage:

## NetworkTick Usage:
- ### NetworkTick.ServerTick: 
    Returns current Tick on the Server
  ```
   int - NetworkTick.ServerTick
  ```
- ### NetworkTick.ClientOffsetTick: 
    Returns the difference between ServerTick and Fast forwarded tick on the client based on RTT (always half RTT converted to ticks)
  ```
   int - NetworkTick.ClientOffsetTick
  ```
- ### NetworkTick.ClientPredictionTick:
  Returns the tick that is currently running on the client ( Server Tick + Client Offset Tick)
  ```
   int - NetworkTick.ClientPredictionTick
  ```

- ### NetworkTick.ServerTime:
  Server Ticks converted to float time based on `Time.fixedDeltaTime`
  ```
   float - NetworkTick.ServerTime
  ```

- ### NetworkTick.ClientOffsetTime:
  Client Tick Offset converted to float time based on `Time.fixedDeltaTime`
  ```
   float - NetworkTick.ClientOffsetTime
  ```

- ### NetworkTick.ClientPredictionTime:
  Client Prediction Tick converted to float time based on `Time.fixedDeltaTime`
  ```
   float - NetworkTick.ClientPredictionTime
  ```

## NetworkController usage
```using NetworkScripts;

public class CustomNetworkController : NetworkController {
  public override void PhysicStep(float deltaTime) {
    // Physics normal operation - 1 delta time forward
    // Will not run when Skip or FastForward are called
    KinematicCharacterSystem.ManualSimulate(deltaTime);
  }

  public override int PhysicStepSkip(int skipSteps, float deltaTime) {
    // The controller will automatically skip steps for you but if you want to reverse physics you should do it here
    return skipSteps - 1;
  }

  public override int PhysicStepFastForward(int fastForwardSteps, float deltaTime) {
    // You can fast forward physics here or you can spread the fast forwarding over time - up to you

    /******************************************************************************/
    /* Please NOTE that you also need to run the normal Physic step here as well! */
    /* -------------------------------------------------------------------------- */
    /****************** deltaTime + deltaTime * fastForwardSteps ******************/
    /* -------------------------------------------------------------------------- */
    /******************************************************************************/
    KinematicCharacterSystem.ManualSimulate(deltaTime + deltaTime * fastForwardSteps);
    return 0;

    /* OR */
    /*
     * KinematicCharacterSystem.ManualSimulate(deltaTime*2);
     * return fastForwardSteps - 2; 
     */
  }
}```