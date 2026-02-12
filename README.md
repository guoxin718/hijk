这是一个记录Windows系统的使用情况的Project，系统运行需要.net framework8.0;

程序开发需要用到vs；

建议将程序的快捷方式放到“运行”目录，这样可以开机自动运行；

系统运行后在桌面右下角图标，并在当前目录自动新建Log目录，共有3个log文件：

1. xxxx-xx-xx_Browser.log中，记录浏览器访问的网址，以及开始访问的时间；

2. xxxx-xx-xx_Applications.log中，记录所有打开过的程序，以及开始使用的时间；

3. xxxx-xx-xx_System.log中，记录本程序的开、关时间；