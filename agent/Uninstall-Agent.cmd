@echo off
rem Mtaalam V2V Agent - removes the guest agent service from this Windows VM.
rem Keep the agent until the VM runs on Proxmox: it restores the static IP settings there.
"%~dp0MtaalamV2VAgent.exe" /uninstall
