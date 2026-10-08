"""The command-line tools installed with carlacontrol, one module per command.

Each module holds one tool's argument parsing and its `main(argv=None)`, which returns the exit
status. `pyproject.toml` installs each as a `carla-*` console command, and the scripts under
`CarlaControl/scripts/` and `CarlaNet/python/run_sumo_drive.py` call the same `main()` from a
checkout:

    carla-sctmv                  sctmv                  build a world and fly, drive and record in it
    carla-build-world            build_world            build a world and write its world package
    carla-compile-scenario       compile_scenario       compile a scenario against its world package
    carla-capture                capture                capture a window of a compiled scenario
    carla-drive                  drive                  drive a world's vehicles from SUMO
    carla-cot-telemetry          cot_telemetry          Cursor-on-Target from a SUMO scenario
    carla-free-camera            free_camera            fly a camera of your own in a running world
    carla-camera-follower        camera_follower        watch a running world through one camera
    carla-audit-sidecars         audit_sidecars         check a capture's truth sidecars
    carla-diff-manifests         diff_manifests         compare two runs' supervision rows
    carla-check-label-leaks      check_label_leaks      check a dataset for what identifies planted
                                                        vehicles
    carla-publish-reference-set  publish_reference_set  publish a world package's reference set
    carla-check-sumo             check_sumo             check which SUMO resolves, and that it is
                                                        complete
    carla-validate               validate               check every file of a capture against its
                                                        schema

Where a command takes its defaults from -- a source checkout, or the current folder when installed --
is `carlacontrol.ToolLayout`.

Every parser names its usage line after the name the command was invoked by (`prog`, the base name
of `sys.argv[0]`): `carla-capture`, or `run_capture.py` from a checkout. Python 3.14 would otherwise
name a console command on Windows by the interpreter and the launcher's full path.
"""
