# Renode-QEMU Co-simulation
Shared memory between Renode and QEMU for Co-simulation

## Usage
### QEMU
1. add memory backend file parameters to the QEMU command line
    - set memory size:
      
      `-m 2G`
    
    - set memory backend file:
      
      `-object memory-backend-file,id=pc.ram,size=2G,mem-path=/dev/shm/qemu-ram,prealloc=on,share=on`
    
    - set memory backend for machine
      
      `-machine memory-backend=pc.ram`
    
    ```shell
    # example
    qemu-system-aarch64 \
    -M virt,gic-version=3 -m 2G \
    -object memory-backend-file,id=pc.ram,size=2G,mem-path=/dev/shm/qemu-ram,prealloc=on,share=on \
    -machine memory-backend=pc.ram \
    -cpu cortex-a53 -smp 4 \
    -serial mon:stdio -nographic \
    -kernel /your/linux/kernel/Image \
    -append "your bootargs" \
    -dtb /your/device/tree/blob.dtb
    ```
2. start QEMU with memory file backend params and check if the file /dev/shm/qemu-ram has been generated
### Renode
1. add SharedMemory peripheral to `your_renode_platform.repl`
    ```repl
    ram: Memory.SharedMemory @ sysbus 0xC0000000
        offset: 0x3c100000
        size: 0x40000000
        filePath: "/dev/shm/qemu-ram"
    ```
2. add SharedMemory.cs plugin to `your_renode_script.resc`
    ```resc
    include $CWD/SharedMemory.cs
    machine LoadPlatformDescription $CWD/your_renode_platform.repl
    ```
3. start Renode emulation after QEMU has booted and created /dev/shm/qemu-ram file