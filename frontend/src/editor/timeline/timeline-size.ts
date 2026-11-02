import { createContext } from 'react';

/**
 * 底部区拖出来的时间轴画布高度（像素，界面改版 §2.6）；undefined 是按轨道数的自然高度。
 * 多出来或少掉的高度都给波形轨，其他轨道高度不变；波形轨最矮 MIN_WAVE。
 */
export const TimelineHeight = createContext<number | undefined>(undefined);
