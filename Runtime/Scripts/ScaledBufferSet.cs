using System;

namespace CallumNicholson.FluidExplosionURP
{
    public class ScaledBufferSet<T, TSize>
    {
        private T[] buffers;

        public ScaledBufferSet(Func<TSize, T> generator, Func<TSize, int, TSize> divisionOp, TSize size, int layers)
        {
            buffers = new T[layers];

            for (int i = 0; i < layers; ++i)
            {
                buffers[i] = generator(divisionOp(size, i + 1));
            }
        }

        public void ForEach(Action<T> action)
        {
            foreach(T buffer in buffers) action(buffer);
        }

        public T this[int layer] => buffers[layer];
    }
}
